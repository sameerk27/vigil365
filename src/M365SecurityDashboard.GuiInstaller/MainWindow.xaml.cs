using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;   // ExtractToFile is an extension method on ZipArchiveEntry
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

            if (IsAdministrator())
            {
                AdminStatus.Text = "✅ Running as Administrator.";
            }
            else
            {
                AdminStatus.Text = "❌ Not running as Administrator.";
                MessageBox.Show(
                    "Please close this and run the installer as an Administrator.\n\n" +
                    "Vigil365 registers a Windows service, creates a SQL login and may install a certificate — " +
                    "none of which are possible without administrator rights.",
                    "Administrator rights required", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnCheckPrereqs.IsEnabled = true;
                return;
            }

            // .NET and Node used to be required because the application was built
            // on the customer's server. It is now built at release time and
            // carried inside this executable, so neither is needed.
            await CheckAndInstallPrerequisite("az", "https://aka.ms/installazurecliwindows", "/i \"{0}\" /quiet /norestart", AzStatus, "msiexec.exe");

            // Catch a payload-less build here rather than after SQL Express has
            // been installed and an Entra application registered.
            var hasPayload = System.Reflection.Assembly.GetExecutingAssembly()
                                 .GetManifestResourceNames().Contains(PayloadResource);
            if (hasPayload)
            {
                PayloadStatus.Text = "✅ Application package is present.";
            }
            else
            {
                PayloadStatus.Text = "❌ Application package is missing.";
                MessageBox.Show(
                    "This installer was built without the Vigil365 application inside it, so it cannot install anything.\n\n" +
                    "Rebuild it with scripts/build-installer.ps1.",
                    "Incomplete installer", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnCheckPrereqs.IsEnabled = true;
                return;
            }

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
            Scope_Changed(this, e);
            TxtTenant.TextChanged += (_, __) => { if (TxtTenant.IsKeyboardFocusWithin) tenantEditedByUser = true; };
            PrefillAdministrator();
            DetectExistingSqlServer();
        }

        /// <summary>
        /// Fills the admin box from the Azure CLI session. The person running the
        /// installer is nearly always the first admin, so asking them to type an
        /// address the machine already knows is a question for its own sake.
        /// </summary>
        /// <summary>
        /// Suggests the signed-in account as a hint only. Deliberately does not
        /// fill the box: the first administrator is a real decision, and a
        /// pre-filled value is the one people click past without reading.
        /// </summary>
        private void PrefillAdministrator()
        {
            // Off the UI thread: "az account show" spawns a process and can take a
            // second or two, which on the UI thread stalls the step transition.
            _ = Task.Run(() =>
            {
                string? upn = null;
                try { upn = RunCommandAndCapture("az", "account show --query user.name -o tsv").Trim(); }
                catch { /* not signed in yet; the plain hint is fine */ }

                Dispatcher.Invoke(() =>
                {
                    TxtAdminEmailHint.Text = !string.IsNullOrWhiteSpace(upn) && upn.Contains('@')
                        ? $"This person gets full access and can add everyone else. You are signed in to Azure as {upn}."
                        : "This person gets full access and can add everyone else.";
                });
            });
        }

        /// <summary>
        /// Reinstalling SQL Express over a working instance is destructive and
        /// slow, so detect one and default to using it.
        /// </summary>
        private void DetectExistingSqlServer()
        {
            try
            {
                // The canonical list of installed instances. Named values are the
                // instance names ("SQLEXPRESS"); MSSQLSERVER is the default one.
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                var instances = key?.GetValueNames() ?? Array.Empty<string>();

                if (instances.Length > 0)
                {
                    var preferred = instances.FirstOrDefault(i => i.Equals("SQLEXPRESS", StringComparison.OrdinalIgnoreCase))
                                    ?? instances[0];
                    var server = preferred.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "." : $".\\{preferred}";
                    TxtSqlDetected.Text = $"Found SQL Server already installed ({string.Join(", ", instances)}). Vigil365 will use it.";
                    ChkInstallSql.IsChecked = false;
                    TxtSqlString.Text = $"Server={server};Database=Vigil365;Trusted_Connection=True;TrustServerCertificate=True";
                }
                else
                {
                    TxtSqlDetected.Text = "No SQL Server found on this computer. Vigil365 will install SQL Server Express (about 5-10 minutes).";
                    ChkInstallSql.IsChecked = true;
                }
            }
            catch { /* detection is a convenience; the checkbox still governs */ }
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

        // True once the operator edits the tenant box, after which it stops
        // tracking the email domain — the two legitimately differ when a tenant
        // uses a vanity mail domain.
        private bool tenantEditedByUser;

        private void TxtAdminEmail_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtTenant == null || tenantEditedByUser) return;
            var at = TxtAdminEmail.Text.LastIndexOf('@');
            TxtTenant.Text = at >= 0 && at < TxtAdminEmail.Text.Length - 1
                ? TxtAdminEmail.Text[(at + 1)..].Trim()
                : "";
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

                // Only preselect something that actually covers the address.
                // Defaulting to whatever happened to be first offers a certificate
                // for the wrong name as though it were the recommendation.
                CmbCertStore.SelectedItem = storeCertificates.FirstOrDefault(c => c.Thumbprint == selected)
                                            ?? storeCertificates.FirstOrDefault(c => c.MatchesHost);
            }
        }

        /// <summary>
        /// Loopback-only evaluation address. Entra permits http for loopback
        /// redirect URIs, which is what lets this mode skip certificates
        /// altogether rather than fobbing the user off with a broken warning.
        /// </summary>
        private const string LocalUrl = "http://localhost:8080";

        private bool IsLocalScope => RadScopeLocal?.IsChecked == true;

        /// <summary>The address the install will actually serve and advertise.</summary>
        private Uri EffectiveUri =>
            IsLocalScope ? new Uri(LocalUrl) : (ParseUrl(TxtUrl.Text) ?? new Uri(LocalUrl));

        private void Scope_Changed(object sender, RoutedEventArgs e)
        {
            if (PanelNetworkSettings == null) return;
            var local = IsLocalScope;
            PanelNetworkSettings.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
            PanelLocalSummary.Visibility    = local ? Visibility.Visible : Visibility.Collapsed;
            if (!local) RefreshCertificateList();
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
            if (string.IsNullOrWhiteSpace(TxtAdminEmail.Text) || !TxtAdminEmail.Text.Contains('@'))
            {
                MessageBox.Show("Enter the email address of the first administrator.");
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtTenant.Text))
            {
                MessageBox.Show("Enter the Microsoft 365 tenant Vigil365 should be registered in.");
                return;
            }

            if (!IsLocalScope && ParseUrl(TxtUrl.Text) == null)
            {
                MessageBox.Show("That address is not a valid URL. Example: https://vigil365.mycompany.com");
                return;
            }

            // Loopback evaluation needs no certificate at all — Entra allows http
            // for loopback redirect URIs.
            if (IsLocalScope)
            {
                certificate = null;
                await StartInstall();
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

            await StartInstall();
        }

        private async Task StartInstall()
        {
            PanelConfig.Visibility = Visibility.Collapsed;
            PanelInstall.Visibility = Visibility.Visible;
            Step2Label.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 255, 255, 255));
            Step3Label.Foreground = System.Windows.Media.Brushes.White;
            Step3Label.FontWeight = FontWeights.Bold;

            // Let WPF actually paint this step before any blocking work starts.
            // Without the yield the whole install ran on the UI thread, so the
            // window stayed frozen on Configuration and then jumped straight to a
            // half-finished progress bar.
            installSqlServer = ChkInstallSql.IsChecked == true;
            existingSqlConnectionString = TxtSqlString.Text;
            plannedLocalOnly = IsLocalScope;
            plannedUri = EffectiveUri;
            plannedAdminEmail = TxtAdminEmail.Text.Trim();
            plannedTenant = TxtTenant.Text.Trim();
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Background);

            await RunInstallationAsync();
        }

        // --- Step 3: Installation ---

        // Captured from the UI before the install begins, so the work itself never
        // touches controls from a background thread.
        private bool installSqlServer;
        private string existingSqlConnectionString = "";
        private bool plannedLocalOnly;
        private Uri plannedUri = new("http://localhost:8080");
        private string plannedAdminEmail = "";
        private string plannedTenant = "";

        private async Task RunInstallationAsync()
        {
            try
            {
                // Ensure Azure login first
                UpdateProgress(10, $"Signing in to {plannedTenant}...");
                Log($"Looking up the Microsoft 365 tenant '{plannedTenant}'...");
                tenantId = await ResolveTenantIdAsync(plannedTenant);
                var tenantForLogin = plannedTenant;
                await Task.Run(() => EnsureAzureLogin(tenantForLogin, tenantId));

                // SQL Setup
                if (installSqlServer)
                {
                    UpdateProgress(20, "Downloading & Installing SQL Server Express...");
                    sqlConnectionString = await SetupSqlServer();
                }
                else
                {
                    sqlConnectionString = existingSqlConnectionString;
                }

                // Without this the service has no SQL login at all and dies on its
                // first connection. Doing it here, while the installer still holds
                // administrator rights, is the only moment it is straightforward.
                UpdateProgress(30, "Preparing the database...");
                try
                {
                    DatabaseSetup.GrantServiceAccess(sqlConnectionString, "NT AUTHORITY\\LOCAL SERVICE", Log);
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        "Could not prepare the database for the Vigil365 service. " +
                        "The service account would not be able to sign in to SQL Server.\r\n\r\n" + ex.Message, ex);
                }

                // App Registration. Must be the canonical origin, not the raw text:
                // Entra matches redirect URIs by exact string.
                UpdateProgress(40, "Creating Azure App Registration...");
                var uri = plannedUri;
                var origin = uri.IsDefaultPort
                    ? $"{uri.Scheme}://{uri.Host}"
                    : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
                await Task.Run(() => RegisterAzureApp(origin));

                // Application files
                UpdateProgress(60, "Installing application files...");
                await InstallApplicationFiles();

                // Service Setup
                UpdateProgress(80, "Configuring Windows Service...");
                await Task.Run(SetupService);

                UpdateProgress(100, "Done!");
                BtnNextToDone.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                ShowInstallFailure(ex);
            }
        }

        /// <summary>
        /// Turns a failure into something the user can act on, and leaves a way
        /// back. Stopping on a raw exception message with the wizard stuck on step
        /// three gives them nothing to do but close it and guess.
        /// </summary>
        private void ShowInstallFailure(Exception ex)
        {
            var message = ex.Message ?? "";
            string title, remedy;

            if (message.Contains("database", StringComparison.OrdinalIgnoreCase)
                || message.Contains("SQL", StringComparison.OrdinalIgnoreCase)
                || message.Contains("login", StringComparison.OrdinalIgnoreCase))
            {
                title = "Vigil365 could not set up its database.";
                remedy =
                    "• Check SQL Server is running: open Services and look for \"SQL Server (SQLEXPRESS)\".\n" +
                    "• Go back and check the connection string names a server you can reach. The default is " +
                    ".\\SQLEXPRESS.\n" +
                    "• You must be a SQL administrator on that instance. If someone else administers SQL Server, " +
                    "ask them to run this installer, or to create a login for NT AUTHORITY\\LOCAL SERVICE with " +
                    "db_owner on the Vigil365 database.\n" +
                    "• Nothing has been installed as a service yet, so it is safe to fix this and try again.";
            }
            else if (message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                     || message.Contains(".pfx", StringComparison.OrdinalIgnoreCase))
            {
                title = "The certificate could not be used.";
                remedy =
                    "• Go back and check the .pfx password.\n" +
                    "• The certificate must include its private key — a .cer or .crt file will not work.\n" +
                    "• If you do not have one to hand, choose \"Create one for me\" to get running now and " +
                    "replace it later.";
            }
            else if (message.Contains("payload", StringComparison.OrdinalIgnoreCase))
            {
                title = "This installer does not contain the Vigil365 application.";
                remedy =
                    "• This build is incomplete and cannot install anything.\n" +
                    "• Download the official Vigil365-Setup.exe, or rebuild it with scripts/build-installer.ps1.";
            }
            else if (message.Contains("az ", StringComparison.OrdinalIgnoreCase)
                     || message.Contains("Entra", StringComparison.OrdinalIgnoreCase)
                     || message.Contains("tenant", StringComparison.OrdinalIgnoreCase)
                     || message.Contains("app registration", StringComparison.OrdinalIgnoreCase))
            {
                title = "Vigil365 could not register itself with Microsoft Entra.";
                remedy =
                    "• Sign in to Azure first: open a terminal and run  az login\n" +
                    "• Your account needs permission to create app registrations (Application Administrator or " +
                    "Cloud Application Administrator). If it does not have that, ask an administrator to run this " +
                    "step, or register the application manually — see the README.\n" +
                    "• Then come back and try again.";
            }
            else
            {
                title = "Installation did not complete.";
                remedy =
                    "• Read the log below for the step that failed.\n" +
                    "• Go back, correct the setting it names, and run the installation again.\n" +
                    "• Use \"Copy details\" to capture the log if you need to send it on.";
            }

            TxtFailureTitle.Text = title + "\n\n" + message.Trim();
            TxtFailureRemedy.Text = remedy;
            PanelFailure.Visibility = Visibility.Visible;
            BtnNextToDone.Visibility = Visibility.Collapsed;

            InstallHeading.Text = "Installation failed";
            InstallStatusText.Text = "Stopped. Nothing further was changed.";
            InstallProgress.Foreground = System.Windows.Media.Brushes.IndianRed;
        }

        private void BtnBackToConfig_Click(object sender, RoutedEventArgs e)
        {
            // Reset the run so a retry starts clean rather than resuming into
            // half-applied state from the previous attempt.
            PanelFailure.Visibility = Visibility.Collapsed;
            InstallHeading.Text = "Installing Vigil365...";
            InstallStatusText.Text = "Starting...";
            InstallProgress.Value = 0;
            InstallProgress.Foreground = System.Windows.Media.Brushes.ForestGreen;
            TxtLog.Clear();
            certificate = null;
            usedSelfSignedCertificate = false;

            PanelInstall.Visibility = Visibility.Collapsed;
            PanelConfig.Visibility = Visibility.Visible;
            Step3Label.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 255, 255, 255));
            Step2Label.Foreground = System.Windows.Media.Brushes.White;
            Step2Label.FontWeight = FontWeights.Bold;
        }

        private void BtnBackToPrereqs_Click(object sender, RoutedEventArgs e)
        {
            PanelConfig.Visibility = Visibility.Collapsed;
            PanelPrerequisites.Visibility = Visibility.Visible;
            Step2Label.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 255, 255, 255));
            Step1Label.Foreground = System.Windows.Media.Brushes.White;
            Step1Label.FontWeight = FontWeights.Bold;
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText($"{TxtFailureTitle.Text}\r\n\r\n--- log ---\r\n{TxtLog.Text}");
                BtnCopyLog.Content = "Copied";
            }
            catch { BtnCopyLog.Content = "Copy failed"; }
        }

        /// <summary>
        /// Resolves a tenant domain to its directory id without needing to be
        /// signed in. The OpenID discovery document is public, and its issuer
        /// carries the tenant GUID.
        ///
        /// Doing this up front means a typo in the tenant is caught before SQL
        /// Server is touched, rather than after.
        /// </summary>
        private async Task<string> ResolveTenantIdAsync(string tenant)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/v2.0/.well-known/openid-configuration";

            HttpResponseMessage res;
            try { res = await http.GetAsync(url); }
            catch (Exception ex) { throw new Exception($"Could not reach Microsoft to look up '{tenant}'. {ex.Message}"); }

            if (!res.IsSuccessStatusCode)
                throw new Exception(
                    $"'{tenant}' is not a Microsoft 365 tenant, or the name is misspelt.\r\n\r\n" +
                    "Use the domain your users sign in with (for example contoso.com), or the " +
                    "tenant's contoso.onmicrosoft.com name.");

            var doc = JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync());
            var issuer = doc.GetProperty("issuer").GetString() ?? "";
            var id = issuer.Split('/', StringSplitOptions.RemoveEmptyEntries)
                           .FirstOrDefault(part => Guid.TryParse(part, out _));

            if (string.IsNullOrEmpty(id))
                throw new Exception($"Microsoft did not return a directory id for '{tenant}'.");
            return id;
        }

        /// <summary>
        /// Signs the Azure CLI in to the tenant this install is for.
        ///
        /// It used to accept whatever tenant the CLI happened to be signed into.
        /// If that was not the administrator's own tenant, the application was
        /// registered in the wrong directory as a single-tenant app — so the
        /// people it was installed for could never sign in, and nothing said so.
        /// </summary>
        private void EnsureAzureLogin(string tenant, string expectedTenantId)
        {
            var current = "";
            try { current = RunCommandAndCapture("az", "account show --query tenantId -o tsv").Trim(); }
            catch { /* not signed in */ }

            if (string.Equals(current, expectedTenantId, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Azure CLI is signed in to {tenant} ({expectedTenantId}).");
                return;
            }

            if (string.IsNullOrEmpty(current))
                Log($"Signing in to {tenant} — a browser window will open...");
            else
                Log($"Azure CLI is signed in to a different tenant ({current}). Switching to {tenant}...");

            // --allow-no-subscriptions because a Microsoft 365 tenant frequently
            // has no Azure subscription, and without it the CLI refuses to
            // complete a sign-in that is otherwise perfectly valid.
            RunCommand("az", $"login --tenant {tenant} --allow-no-subscriptions");

            try { current = RunCommandAndCapture("az", "account show --query tenantId -o tsv").Trim(); }
            catch { current = ""; }

            if (!string.Equals(current, expectedTenantId, StringComparison.OrdinalIgnoreCase))
                throw new Exception(
                    $"Signed in to the wrong Microsoft 365 tenant.\r\n\r\n" +
                    $"Expected {tenant} ({expectedTenantId}) but the Azure CLI is in " +
                    $"{(string.IsNullOrEmpty(current) ? "no tenant" : current)}.\r\n\r\n" +
                    "Sign in with an account in that tenant when the browser opens. " +
                    "If you were already signed in as someone else, run  az logout  first.");

            Log($"Signed in to {tenant} ({expectedTenantId}).");
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
            // tenantId was resolved from the administrator's tenant and the CLI was
            // signed in to it, so it is not re-read from the ambient context here.
            // Reading it from "az account show" is what silently registered the
            // application in whichever directory the CLI happened to be pointed at.
            if (string.IsNullOrEmpty(tenantId)) throw new Exception("Tenant was not resolved before registration.");
            Log($"Registering in tenant {tenantId}...");

            // Reuse an existing registration rather than minting another one.
            // Re-running the wizard is a documented action — it is how you replace
            // the certificate or change the address — and creating a fresh app
            // every time would litter the tenant with near-identical registrations
            // and silently strand whichever one was configured last.
            string? objectId = null;
            try
            {
                var existingJson = RunCommandAndCapture("az",
                    "ad app list --display-name \"Vigil365\" --query \"[0]\" -o json").Trim();
                if (!string.IsNullOrEmpty(existingJson) && existingJson != "null")
                {
                    var existing = JsonSerializer.Deserialize<JsonElement>(existingJson);
                    if (existing.ValueKind == JsonValueKind.Object)
                    {
                        clientId = existing.GetProperty("appId").GetString();
                        objectId = existing.GetProperty("id").GetString();
                        Log($"Reusing the existing Vigil365 app registration ({clientId}).");
                    }
                }
            }
            catch { /* fall through to creating one */ }

            if (objectId == null)
            {
                Log("Creating Entra Application...");
                var appJson = RunCommandAndCapture("az", "ad app create --display-name \"Vigil365\" --sign-in-audience AzureADMyOrg");

                var app = JsonSerializer.Deserialize<JsonElement>(appJson);
                clientId = app.GetProperty("appId").GetString();
                objectId = app.GetProperty("id").GetString();

                Log($"Created App Registration. Client ID: {clientId}");
            }

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

        private const string PayloadResource = "Vigil365.payload.zip";

        /// <summary>
        /// Unpacks the application carried inside this executable.
        ///
        /// The build used to happen here — npm install, npm run build, dotnet
        /// publish — which meant every customer needed the source tree, Node and
        /// the .NET SDK on their server, and got a different build depending on
        /// what their toolchain resolved that day. It is all done at release time
        /// now (scripts/build-installer.ps1) and shipped as a compressed payload.
        /// </summary>
        private async Task InstallApplicationFiles()
        {
            var publishPath = @"C:\Program Files\Vigil365";

            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            await using var payload = asm.GetManifestResourceStream(PayloadResource);
            if (payload == null)
                throw new Exception(
                    "This installer was built without the application payload, so there is nothing to install. " +
                    "Rebuild it with scripts/build-installer.ps1.");

            // An upgrade over a running service holds locks on the very files
            // being replaced, and the extraction failure that produces reads as
            // file corruption rather than "it is still running".
            Log("Stopping any running Vigil365 service...");
            RunCommand("sc", "stop Vigil365");
            await Task.Delay(3000);

            Log($"Extracting the application to {publishPath}...");
            Directory.CreateDirectory(publishPath);

            await Task.Run(() =>
            {
                using var archive = new System.IO.Compression.ZipArchive(
                    payload, System.IO.Compression.ZipArchiveMode.Read);

                foreach (var entry in archive.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(publishPath, entry.FullName));

                    // Refuse entries that escape the install directory. A zip is
                    // an untrusted format even when we built it, and this is the
                    // check whose absence is the classic path-traversal bug.
                    if (!target.StartsWith(Path.GetFullPath(publishPath) + Path.DirectorySeparatorChar,
                                           StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Refusing to extract outside the install folder: {entry.FullName}");

                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            });

            var exe = Path.Combine(publishPath, "M365SecurityDashboard.Api.exe");
            if (!File.Exists(exe)) throw new Exception($"Extraction finished but {exe} is missing.");
            Log("Application files installed.");
        }

        private void SetupService()
        {
            var publishPath = @"C:\Program Files\Vigil365";
            var adminEmail = plannedAdminEmail;

            // DataProtection keys must live somewhere the service account can
            // WRITE. They previously went under Program Files, which is read-only
            // for LOCAL SERVICE — the keyring could never be persisted, so every
            // restart invalidated anything protected with it, including the Graph
            // client secret saved on the Setup page.
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Vigil365");
            var keyPath = Path.Combine(dataDir, "keys");

            var localOnly = plannedLocalOnly;
            var uri = plannedUri;
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
            // CORS while nothing ever listened on it. Bind what we advertise —
            // and for the evaluation mode, bind loopback only so "just this
            // computer" is enforced rather than merely promised.
            var scheme = isHttps ? "https" : "http";
            var bindHost = localOnly ? "127.0.0.1" : "*";
            var kestrel = new System.Text.StringBuilder();
            kestrel.AppendLine("    \"Kestrel\": {");
            kestrel.AppendLine("        \"Endpoints\": {");
            kestrel.AppendLine("            \"Public\": {");
            kestrel.Append($"                \"Url\": \"{scheme}://{bindHost}:{port}\"");
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
                    "KeyPath": "{{keyPath.Replace("\\", "\\\\")}}"
                }
            }
            """;

            Log("Writing Configuration File...");
            File.WriteAllText(Path.Combine(publishPath, "appsettings.Production.json"), configJson);

            const string serviceAccount = "NT AUTHORITY\\LOCAL SERVICE";

            Directory.CreateDirectory(keyPath);
            try
            {
                var acl = new DirectoryInfo(keyPath).GetAccessControl();
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    serviceAccount,
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                    System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                new DirectoryInfo(keyPath).SetAccessControl(acl);
                Log($"Data protection keys will be stored in {keyPath}.");
            }
            catch (Exception ex)
            {
                Log($"Could not grant write access to {keyPath} ({ex.Message}). Saving Graph credentials may fail.");
            }

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
            // exactly like a broken install. Loopback-only installs need no hole
            // punched, and opening one would contradict "just this computer".
            RunCommand("netsh", "advfirewall firewall delete rule name=\"Vigil365\"");
            if (!localOnly)
            {
                Log($"Opening the firewall for inbound TCP {port}...");
                RunCommand("netsh", $"advfirewall firewall add rule name=\"Vigil365\" dir=in action=allow protocol=TCP localport={port}");
            }
            else
            {
                Log("Loopback-only install — no firewall change needed.");
            }

            Log("Installing Windows Service...");
            RunSc($"stop {serviceName}", check: false);      // not running yet on a first install
            RunSc($"delete {serviceName}", check: false);
            System.Threading.Thread.Sleep(2000);

            // The binPath needs the executable quoted because "Program Files"
            // contains a space, and that inner quote has to survive being nested
            // inside the quoted binPath value — hence \" here. Routing this
            // through cmd.exe re-parses the quotes and hands sc a truncated path
            // ("binPath= ""C:\Program"), which is why RunSc invokes sc.exe
            // directly.
            //
            // No --urls: it overrides Kestrel's configured endpoints, which is how
            // the certificate would get silently ignored.
            var binPath = $"\\\"{exePath}\\\" --environment Production";
            RunSc($"create {serviceName} binPath= \"{binPath}\" start= auto obj= \"NT AUTHORITY\\LocalService\"");
            RunSc($"description {serviceName} \"Vigil365 Microsoft 365 security monitoring service\"", check: false);
            RunSc($"failure {serviceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000", check: false);

            Log("Starting Service...");
            RunSc($"start {serviceName}");

            // Confirm it is actually running. Previously nothing checked, so a
            // service that was never created — or that started and immediately
            // died — still reached the "Installation Complete!" screen, and the
            // first sign of trouble was the browser refusing to connect.
            VerifyServiceRunning(serviceName);
        }

        /// <summary>
        /// Runs sc.exe directly and, unless told otherwise, fails loudly.
        /// </summary>
        private void RunSc(string arguments, bool check = true)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            var output = (stdout + stderr).Trim();
            if (p.ExitCode != 0)
            {
                if (!check) return;   // expected for stop/delete on a clean machine
                throw new Exception(
                    $"The Windows service command failed (sc {arguments.Split(' ')[0]}, exit code {p.ExitCode}).\r\n\r\n{output}");
            }
            if (!string.IsNullOrWhiteSpace(output)) Log("   " + output.Replace("\n", "\n   ").Trim());
        }

        /// <summary>
        /// Waits for the service to reach Running. A service that dies on startup
        /// reports success from "sc start" and then stops a second later, so the
        /// exit code alone proves nothing.
        /// </summary>
        private void VerifyServiceRunning(string serviceName)
        {
            for (var attempt = 0; attempt < 15; attempt++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"query {serviceName}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi)!;
                var text = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                if (text.Contains("RUNNING")) { Log($"Service '{serviceName}' is running."); return; }
                if (text.Contains("STOPPED") && attempt > 2) break;
                System.Threading.Thread.Sleep(1000);
            }

            throw new Exception(
                $"The '{serviceName}' service was installed but is not running. " +
                "It usually means it could not reach SQL Server, could not read its certificate, " +
                "or the port is already in use. Windows Event Viewer > Windows Logs > Application " +
                "records the reason.");
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