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

        private async void BtnStartInstall_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtUrl.Text) || string.IsNullOrWhiteSpace(TxtAdminEmail.Text))
            {
                MessageBox.Show("Please fill out all fields.");
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
            var publicUrl = TxtUrl.Text;
            var adminEmail = TxtAdminEmail.Text;

            var configJson = $$"""
            {
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
                    "AllowedOrigins": [ "{{publicUrl.TrimEnd('/')}}" ]
                },
                "Security": {
                    "RequireHttps": false
                },
                "DataProtection": {
                    "KeyPath": "{{publishPath.Replace("\\", "\\\\")}}\\keys"
                }
            }
            """;

            Log("Writing Configuration File...");
            File.WriteAllText(Path.Combine(publishPath, "appsettings.Production.json"), configJson);
            Directory.CreateDirectory(Path.Combine(publishPath, "keys"));

            var exePath = Path.Combine(publishPath, "M365SecurityDashboard.Api.exe");
            var serviceName = "Vigil365";

            Log("Installing Windows Service...");
            RunCommand("sc", $"stop {serviceName}");
            RunCommand("sc", $"delete {serviceName}");
            System.Threading.Thread.Sleep(2000);

            var binPath = $"\"{exePath}\" --environment Production --urls http://127.0.0.1:8080";
            RunCommand("sc", $"create {serviceName} binPath= \"{binPath}\" start= auto obj= \"NT AUTHORITY\\LocalService\"");
            RunCommand("sc", $"description {serviceName} \"Vigil365 Microsoft 365 security monitoring service\"");
            RunCommand("sc", $"failure {serviceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000");
            
            Log("Starting Service...");
            RunCommand("sc", $"start {serviceName}");
        }

        private void BtnNextToDone_Click(object sender, RoutedEventArgs e)
        {
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
                FileName = TxtUrl.Text,
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