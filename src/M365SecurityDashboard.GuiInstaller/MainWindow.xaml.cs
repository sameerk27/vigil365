using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Security.Principal;

namespace M365SecurityDashboard.GuiInstaller
{
    public partial class MainWindow : Window
    {
        private string repoRoot = string.Empty;
        private string tenantId = string.Empty;
        private string clientId = string.Empty;
        private string sqlConnectionString = string.Empty;

        private List<StoreCertificate> storeCertificates = new();

        // Null until resolved. For the self-signed option it stays null until the
        // install runs, because generating it needs the target folder to exist.
        private CertificateBinding? certificate;
        private bool usedSelfSignedCertificate;

        // The normalised origin the service actually serves, as opposed to whatever
        // was typed into the box.
        private string installedUrl = "";

        public MainWindow()
        {
            InitializeComponent();
            repoRoot = GetRepoRoot();
        }

        private string GetRepoRoot()
        {
            var current = AppDomain.CurrentDomain.BaseDirectory;
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current, "src", "M365SecurityDashboard.Api")))
                    return current;
                current = Directory.GetParent(current)?.FullName;
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText(message + Environment.NewLine);
                TxtLog.ScrollToEnd();
            });
        }

        private void UpdateProgress(int value, string text)
        {
            Dispatcher.Invoke(() =>
            {
                InstallProgress.Value = value;
                InstallStatusText.Text = text;
            });
        }

        // --- Step 1: Prerequisites ---

        private async void BtnCheckPrereqs_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckPrereqs.IsEnabled = false;

            if (!IsAdministrator())
            {
                MessageBox.Show("Please run this installer as an Administrator.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await CheckAndInstallPrerequisite("dotnet", "https://download.visualstudio.microsoft.com/download/pr/45f9c464-90e6-4ea1-b25f-22fb8e57f00d/74c5d5ad14c9c7198bb602c31e4e4604/dotnet-sdk-8.0.303-win-x64.exe", "/install /quiet /norestart", DotnetStatus);
            await CheckAndInstallPrerequisite("npm", "https://nodejs.org/dist/v20.15.1/node-v20.15.1-x64.msi", "/i \"{0}\" /quiet /norestart", NodeStatus, "msiexec.exe");
            await CheckAndInstallPrerequisite("az", "https://aka.ms/installazurecliwindows", "/i \"{0}\" /quiet /norestart", AzStatus, "msiexec.exe");

            BtnNextToConfig.Visibility = Visibility.Visible;
            BtnCheckPrereqs.Visibility = Visibility.Collapsed;
        }

        private async Task CheckAndInstallPrerequisite(string command, string downloadUrl, string installArgsTemplate, TextBlock statusBlock, string installerExe = null)
        {
            statusBlock.Text = $"⌛ Checking {command}...";
            if (IsCommandAvailable(command))
            {
                statusBlock.Text = $"✅ {command} is installed.";
                return;
            }

            statusBlock.Text = $"⏳ Installing {command} (This may take a minute)...";
            try
            {
                using var client = new HttpClient();
                var tempFile = Path.Combine(Path.GetTempPath(), Path.GetFileName(downloadUrl.Split('?')[0]));
                if (!tempFile.Contains(".")) tempFile += ".exe";

                var response = await client.GetAsync(downloadUrl);
                using (var fs = new FileStream(tempFile, FileMode.Create))
                {
                    await response.Content.CopyToAsync(fs);
                }

                var exe = installerExe ?? tempFile;
                var args = string.Format(installArgsTemplate, tempFile);

                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                await process.WaitForExitAsync();

                statusBlock.Text = $"✅ {command} installed.";
                var newPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
                Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Process);
            }
            catch (Exception ex)
            {
                statusBlock.Text = $"❌ Failed to install {command}.";
                MessageBox.Show(ex.Message);
            }
        }

        private bool IsCommandAvailable(string command)
        {
            try
            {
                var p = new Process();
                p.StartInfo.FileName = "cmd.exe";
                p.StartInfo.Arguments = $"/c where {command}";
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.Start();
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private void BtnNextToConfig_Click(object sender, RoutedEventArgs e)
        {
            PanelPrerequisites.Visibility = Visibility.Collapsed;
            PanelConfig.Visibility = Visibility.Visible;
            Step1Label.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 255, 255, 255));
            Step2Label.Foreground = System.Windows.Media.Brushes.White;
            Step2Label.FontWeight = FontWeights.Bold;

            RefreshCertificateList();
            CertOption_Changed(this, e);
        }

        // --- Step 2: Configuration ---

        private void ChkInstallSql_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelSqlString != null) PanelSqlString.Visibility = Visibility.Collapsed;
        }

        private void ChkInstallSql_Unchecked(object sender, RoutedEventArgs e)
        {
            if (PanelSqlString != null) PanelSqlString.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Splits the address the admin typed into the pieces the install needs.
        /// Returns null when it is not a usable absolute URL.
        /// </summary>
        private static Uri? ParseUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!text.Contains("://")) text = "https://" + text.Trim();
            return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                   ? uri : null;
        }

        private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (UrlHint == null) return;
            var uri = ParseUrl(TxtUrl.Text);

            if (uri == null)
            {
                UrlHint.Text = "";
            }
            else if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            {
                // Worth saying now rather than letting them discover it at the
                // sign-in screen: Entra rejects non-loopback http redirect URIs
                // outright, so this configuration can never sign anyone in.
                UrlHint.Text = "Microsoft sign-in will not work over http:// on a named host. Use https:// instead.";
            }
            else
            {
                UrlHint.Text = "";
            }

            RefreshCertificateList();
        }

        private void RefreshCertificateList()
        {
            if (CmbCertStore == null) return;
            var host = ParseUrl(TxtUrl.Text)?.Host ?? "";
            var selected = (CmbCertStore.SelectedItem as StoreCertificate)?.Thumbprint;

            storeCertificates = CertificateSetup.ListUsable(host);
            CmbCertStore.ItemsSource = storeCertificates;

            if (storeCertificates.Count == 0)
            {
                RadCertStore.IsEnabled = false;
                RadCertStore.Content = "Use a certificate already installed on this server (none found)";
                if (RadCertStore.IsChecked == true) RadCertSelfSigned.IsChecked = true;
            }
            else
            {
                RadCertStore.IsEnabled = true;
                RadCertStore.Content = "Use a certificate already installed on this server";
                CmbCertStore.SelectedItem = storeCertificates.FirstOrDefault(c => c.Thumbprint == selected)
                                            ?? storeCertificates[0];
            }
        }

        private void CertOption_Changed(object sender, RoutedEventArgs e)
        {
            if (CmbCertStore == null) return;
            CmbCertStore.IsEnabled   = RadCertStore.IsChecked == true;
            TxtPfxPath.IsEnabled     = RadCertPfx.IsChecked == true;
            BtnBrowsePfx.IsEnabled   = RadCertPfx.IsChecked == true;
            TxtPfxPassword.IsEnabled = RadCertPfx.IsChecked == true;
        }

        private void BtnBrowsePfx_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select a certificate",
                Filter = "Certificate files (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true) TxtPfxPath.Text = dialog.FileName;
        }

        private async void BtnStartInstall_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtUrl.Text) || string.IsNullOrWhiteSpace(TxtAdminEmail.Text))
            {
                MessageBox.Show("Please fill out all fields.");
                return;
            }

            var uri = ParseUrl(TxtUrl.Text);
            if (uri == null)
            {
                MessageBox.Show("That address is not a valid URL. Example: https://vigil365.mycompany.com");
                return;
            }

            // Resolve the certificate BEFORE anything is installed. A bad password
            // or a key-less .pfx discovered after the service is registered leaves
            // a half-built install and an event-log entry to go hunting for.
            try
            {
                if (RadCertStore.IsChecked == true)
                {
                    if (CmbCertStore.SelectedItem is not StoreCertificate chosen)
                    {
                        MessageBox.Show("Pick a certificate from the list, or choose another option.");
                        return;
                    }
                    certificate = CertificateSetup.FromStore(chosen);
                }
                else if (RadCertPfx.IsChecked == true)
                {
                    if (string.IsNullOrWhiteSpace(TxtPfxPath.Text))
                    {
                        MessageBox.Show("Choose a .pfx file, or select another option.");
                        return;
                    }
                    certificate = CertificateSetup.FromPfx(TxtPfxPath.Text, TxtPfxPassword.Password);
                }
                else
                {
                    certificate = null; // generated during install, once the target folder exists
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"That certificate could not be used:\n\n{ex.Message}", "Certificate problem");
                return;
            }

            PanelConfig.Visibility = Visibility.Collapsed;
            PanelInstall.Visibility = Visibility.Visible;
            Step2Label.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 255, 255, 255));
            Step3Label.Foreground = System.Windows.Media.Brushes.White;
            Step3Label.FontWeight = FontWeights.Bold;

            await RunInstallationAsync();
        }

        // --- Step 3: Installation ---

        private async Task RunInstallationAsync()
        {
            try
            {
                // Ensure Azure login first
                UpdateProgress(10, "Checking Azure Login...");
                EnsureAzureLogin();

                // SQL Setup
                if (ChkInstallSql.IsChecked == true)
                {
                    UpdateProgress(20, "Downloading & Installing SQL Server Express...");
                    sqlConnectionString = await SetupSqlServer();
                }
                else
                {
                    sqlConnectionString = TxtSqlString.Text;
                }

                // App Registration
                UpdateProgress(40, "Creating Azure App Registration...");
                RegisterAzureApp(TxtUrl.Text);

                // Build
                UpdateProgress(60, "Building Application...");
                await BuildApplication();

                // Service Setup
                UpdateProgress(80, "Configuring Windows Service...");
                SetupService();

                UpdateProgress(100, "Done!");
                BtnNextToDone.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                MessageBox.Show($"Installation failed: {ex.Message}");
            }
        }

        private void EnsureAzureLogin()
        {
            var p = new Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = "/c az account show";
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                Log("Not logged into Azure CLI. Launching browser to authenticate...");
                RunCommand("az", "login");
            }
        }

        private async Task<string> SetupSqlServer()
        {
            Log("Downloading SQL Server 2022 Express bootstrapper...");
            var downloadUrl = "https://go.microsoft.com/fwlink/p/?linkid=2215158"; 
            var tempFile = Path.Combine(Path.GetTempPath(), "SQL2022-SSEI-Expr.exe");

            using var client = new HttpClient();
            var response = await client.GetAsync(downloadUrl);
            using (var fs = new FileStream(tempFile, FileMode.Create))
            {
                await response.Content.CopyToAsync(fs);
            }

            Log("Running SQL Server Express installation (this may take 5-10 minutes)...");
            var args = "/Q /ACTION=Install /FEATURES=SQL /INSTANCENAME=SQLEXPRESS /SQLSVCACCOUNT=\"NT AUTHORITY\\Network Service\" /SQLSYSADMINACCOUNTS=\"BUILTIN\\ADMINISTRATORS\" /AGTSVCACCOUNT=\"NT AUTHORITY\\Network Service\" /IACCEPTSQLSERVERLICENSETERMS";
            
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = tempFile,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas"
            });
            await p.WaitForExitAsync();

            if (p.ExitCode != 0 && p.ExitCode != 3010)
            {
                throw new Exception($"SQL Server installation failed with exit code: {p.ExitCode}");
            }

            Log("SQL Server installed successfully.");
            return @"Server=.\SQLEXPRESS;Database=Vigil365;Trusted_Connection=True;TrustServerCertificate=True";
        }

        private void RegisterAzureApp(string publicUrl)
        {
            Log("Retrieving Tenant ID...");
            tenantId = RunCommandAndCapture("az", "account show --query tenantId -o tsv").Trim();
            if (string.IsNullOrEmpty(tenantId)) throw new Exception("Could not retrieve tenant ID");

            Log("Creating Entra Application...");
            var appJson = RunCommandAndCapture("az", "ad app create --display-name \"Vigil365\" --sign-in-audience AzureADMyOrg");
            
            var app = JsonSerializer.Deserialize<JsonElement>(appJson);
            clientId = app.GetProperty("appId").GetString();
            var objectId = app.GetProperty("id").GetString();

            Log($"Created App Registration. Client ID: {clientId}");

            var patchJson = $$"""
            {
                "spa": { "redirectUris": [ "{{publicUrl}}" ] },
                "identifierUris": [ "api://{{clientId}}" ],
                "api": {
                    "oauth2PermissionScopes": [{
                        "id": "{{Guid.NewGuid()}}",
                        "type": "User",
                        "value": "access_as_user",
                        "isEnabled": true,
                        "adminConsentDisplayName": "Access Vigil365",
                        "adminConsentDescription": "Allows the signed-in user to access Vigil365 on their behalf.",
                        "userConsentDisplayName": "Access Vigil365",
                        "userConsentDescription": "Allows you to access Vigil365 on your behalf."
                    }]
                }
            }
            """;

            var tempPatch = Path.GetTempFileName();
            File.WriteAllText(tempPatch, patchJson);
            
            Log("Configuring Redirect URIs and Scopes...");
            RunCommand("az", $"rest --method PATCH --uri \"https://graph.microsoft.com/v1.0/applications/{objectId}\" --headers \"Content-Type=application/json\" --body \"@{tempPatch}\"");
            File.Delete(tempPatch);

            RunCommand("az", $"ad sp create --id {clientId}");

            Log("Attempting to grant Admin Consent for Graph API...");
            try
            {
                RunCommand("az", $"ad app permission admin-consent --id {clientId}");
            }
            catch 
            {
                Log("WARNING: Could not grant admin consent automatically. You may need to do this in the Azure Portal.");
            }
        }

        private async Task BuildApplication()
        {
            var clientPath = Path.Combine(repoRoot, "src", "m365-security-dashboard-client");
            Log("Installing NPM dependencies...");
            await RunCommandAsync("npm", "install --no-audit --no-fund", clientPath);
            Log("Building frontend bundle...");
            await RunCommandAsync("npm", "run build", clientPath);

            var apiPath = Path.Combine(repoRoot, "src", "M365SecurityDashboard.Api");
            var publishPath = @"C:\Program Files\Vigil365";
            Log("Publishing .NET Backend...");
            await RunCommandAsync("dotnet", $"publish -c Release -o \"{publishPath}\"", apiPath);
        }

        private void SetupService()
        {
            var publishPath = @"C:\Program Files\Vigil365";
            var adminEmail = TxtAdminEmail.Text;

            var uri = ParseUrl(TxtUrl.Text)!;
            var hostname = uri.Host;
            var port = uri.Port;
            var isHttps = uri.Scheme == Uri.UriSchemeHttps;
            // Canonical origin. Entra matches redirect URIs by exact string, so the
            // default port must not appear: https://host, never https://host:443.
            var publicUrl = uri.IsDefaultPort
                ? $"{uri.Scheme}://{hostname}"
                : $"{uri.Scheme}://{hostname}:{port}";
            installedUrl = publicUrl;

            // The self-signed option is deferred to here because generating the
            // file needs the install directory to exist.
            if (isHttps && certificate == null)
            {
                Directory.CreateDirectory(publishPath);
                certificate = CertificateSetup.CreateSelfSigned(hostname, publishPath, Log);
                usedSelfSignedCertificate = true;
            }

            // Previously this bound http://127.0.0.1:8080 regardless of what was
            // typed, so any hostname the admin entered was written into Entra and
            // CORS while nothing ever listened on it. Bind what we advertise.
            var scheme = isHttps ? "https" : "http";
            var kestrel = new System.Text.StringBuilder();
            kestrel.AppendLine("    \"Kestrel\": {");
            kestrel.AppendLine("        \"Endpoints\": {");
            kestrel.AppendLine("            \"Public\": {");
            kestrel.Append($"                \"Url\": \"{scheme}://*:{port}\"");
            if (certificate != null)
            {
                kestrel.AppendLine(",");
                kestrel.AppendLine("                \"Certificate\": {");
                kestrel.AppendLine(certificate.Json.TrimEnd());
                kestrel.AppendLine("                }");
            }
            else kestrel.AppendLine();
            kestrel.AppendLine("            }");
            kestrel.AppendLine("        }");
            kestrel.Append("    },");
            var kestrelJson = kestrel.ToString();

            var configJson = $$"""
            {
            {{kestrelJson}}
                "ConnectionStrings": {
                    "DefaultConnection": "{{sqlConnectionString.Replace("\\", "\\\\")}}"
                },
                "AzureAd": {
                    "Instance": "https://login.microsoftonline.com/",
                    "TenantId": "{{tenantId}}",
                    "ClientId": "{{clientId}}",
                    "Audience": "api://{{clientId}}"
                },
                "Auth": {
                    "RedirectUri": "{{publicUrl}}",
                    "BootstrapAdminEmail": "{{adminEmail}}"
                },
                "Cors": {
                    "AllowedOrigins": [ "{{publicUrl}}" ]
                },
                "Security": {
                    "RequireHttps": {{(isHttps ? "true" : "false")}}
                },
                "DataProtection": {
                    "KeyPath": "{{publishPath.Replace("\\", "\\\\")}}\\keys"
                }
            }
            """;

            Log("Writing Configuration File...");
            File.WriteAllText(Path.Combine(publishPath, "appsettings.Production.json"), configJson);
            Directory.CreateDirectory(Path.Combine(publishPath, "keys"));

            const string serviceAccount = "NT AUTHORITY\\LOCAL SERVICE";

            // A certificate the service account cannot read is the same as no
            // certificate: the service registers, then dies on startup.
            if (certificate?.Thumbprint != null)
                CertificateSetup.GrantKeyAccess(certificate.Thumbprint, serviceAccount, Log);
            if (certificate?.PfxPath != null)
            {
                try { CertificateSetup.GrantRead(certificate.PfxPath, serviceAccount); }
                catch (Exception ex) { Log($"Could not grant read access to the certificate file ({ex.Message})."); }
            }

            if (certificate != null) Log($"HTTPS will use the {certificate.Description}.");

            var exePath = Path.Combine(publishPath, "M365SecurityDashboard.Api.exe");
            var serviceName = "Vigil365";

            // Windows Firewall blocks inbound by default, so without this the site
            // is reachable from the server itself and nowhere else — which looks
            // exactly like a broken install.
            Log($"Opening the firewall for inbound TCP {port}...");
            RunCommand("netsh", $"advfirewall firewall delete rule name=\"Vigil365\"");
            RunCommand("netsh", $"advfirewall firewall add rule name=\"Vigil365\" dir=in action=allow protocol=TCP localport={port}");

            Log("Installing Windows Service...");
            RunCommand("sc", $"stop {serviceName}");
            RunCommand("sc", $"delete {serviceName}");
            System.Threading.Thread.Sleep(2000);

            // No --urls: it overrides Kestrel's configured endpoints, which is how
            // the certificate would get silently ignored.
            var binPath = $"\"{exePath}\" --environment Production";
            RunCommand("sc", $"create {serviceName} binPath= \"{binPath}\" start= auto obj= \"NT AUTHORITY\\LocalService\"");
            RunCommand("sc", $"description {serviceName} \"Vigil365 Microsoft 365 security monitoring service\"");
            RunCommand("sc", $"failure {serviceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000");
            
            Log("Starting Service...");
            RunCommand("sc", $"start {serviceName}");
        }

        private void BtnNextToDone_Click(object sender, RoutedEventArgs e)
        {
            TxtDoneAddress.Text = $"Vigil365 is available at {installedUrl}";

            if (usedSelfSignedCertificate)
            {
                // Said plainly, because the consequence is specific: this machine
                // is fine, everyone else sees a warning. On a security product a
                // warning people are told to ignore is worse than the warning.
                PanelCertWarning.Visibility = Visibility.Visible;
                TxtCertWarning.Text =
                    "Vigil365 created its own certificate, and trusted it on this server. Browsers on " +
                    "other machines will show a security warning until you replace it.\n\n" +
                    "To replace it, get a certificate for this hostname from your organisation's " +
                    "certificate authority (your IT team can issue one), then re-run this installer and " +
                    "choose \"Use a certificate already installed on this server\" or point it at the .pfx file.\n\n" +
                    "If this server is reachable from the internet, scripts\\request-cert.ps1 can get a " +
                    "free certificate from Let's Encrypt instead.";
            }

            PanelInstall.Visibility = Visibility.Collapsed;
            PanelDone.Visibility = Visibility.Visible;
            Step3Label.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 255, 255, 255));
            Step4Label.Foreground = System.Windows.Media.Brushes.White;
            Step4Label.FontWeight = FontWeights.Bold;
        }

        private void BtnOpenApp_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(installedUrl) ? TxtUrl.Text : installedUrl,
                UseShellExecute = true
            });
            this.Close();
        }

        // --- Helpers ---

        private void RunCommand(string fileName, string arguments, string workingDirectory = null)
        {
            var p = new Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = $"/c {fileName} {arguments}";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            if (workingDirectory != null) p.StartInfo.WorkingDirectory = workingDirectory;
            
            p.Start();
            p.WaitForExit();
        }

        private async Task RunCommandAsync(string fileName, string arguments, string workingDirectory = null)
        {
            var p = new Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = $"/c {fileName} {arguments}";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            if (workingDirectory != null) p.StartInfo.WorkingDirectory = workingDirectory;

            p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
        }

        private string RunCommandAndCapture(string fileName, string arguments)
        {
            var p = new Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = $"/c {fileName} {arguments}";
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }
    }
}