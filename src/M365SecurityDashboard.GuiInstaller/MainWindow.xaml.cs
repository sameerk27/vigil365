using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;   // ExtractToFile is an extension method on ZipArchiveEntry
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Security.AccessControl;
using System.Security.Principal;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

namespace M365SecurityDashboard.GuiInstaller
{
    public partial class MainWindow : Window
    {
        private string tenantId = string.Empty;
        private string clientId = string.Empty;
        private string sqlConnectionString = string.Empty;

        // App-only credential the collector uses against Graph. Empty if the
        // tenant refused to mint one, in which case Setup asks for it.
        private string graphClientSecret = string.Empty;

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
            DetectExistingSqlServer();
            PreselectFromExistingInstall(); // after detection, so a real previous config wins
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

        private void ChkInstallSql_Checked(object sender, RoutedEventArgs e) => RefreshDatabasePanels();
        private void ChkInstallSql_Unchecked(object sender, RoutedEventArgs e) => RefreshDatabasePanels();
        private void Db_Changed(object sender, RoutedEventArgs e) => RefreshDatabasePanels();

        private EditionChoice SelectedEdition => RadModeMsp?.IsChecked == true ? EditionChoice.Msp : EditionChoice.Single;
        private DbEngine SelectedEngine => RadDbPostgres?.IsChecked == true ? DbEngine.Postgres : DbEngine.SqlServer;

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtModeNote == null || ChkInstallSql == null) return;
            // MSP mode never installs SQL Express (InstallPlan.DatabaseProblem).
            if (SelectedEdition == EditionChoice.Msp && ChkInstallSql.IsChecked == true) ChkInstallSql.IsChecked = false;
            TxtModeNote.Text = SelectedEdition == EditionChoice.Msp && existingInstall?.Edition == EditionChoice.Single
                ? "This converts the existing install to MSP: same app registration, same data - your current tenant becomes the first client."
                : "";
            RefreshDatabasePanels();
        }

        /// <summary>One place decides which database inputs are visible.</summary>
        private void RefreshDatabasePanels()
        {
            if (PanelSqlString == null || PanelPgString == null || ChkInstallSql == null) return;
            var postgres = SelectedEngine == DbEngine.Postgres;
            var msp = SelectedEdition == EditionChoice.Msp;
            ChkInstallSql.Visibility = postgres || msp ? Visibility.Collapsed : Visibility.Visible;
            if ((postgres || msp) && ChkInstallSql.IsChecked == true) ChkInstallSql.IsChecked = false;
            PanelPgString.Visibility = postgres ? Visibility.Visible : Visibility.Collapsed;
            PanelSqlString.Visibility = !postgres && ChkInstallSql.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The config of a previous install, if any (preselects the wizard on re-run).</summary>
        private InstallPlan.ExistingInstall? existingInstall;

        private void PreselectFromExistingInstall()
        {
            try
            {
                var path = Path.Combine(@"C:\Program Files\Vigil365", "appsettings.Production.json");
                existingInstall = File.Exists(path) ? InstallPlan.ReadExisting(File.ReadAllText(path)) : null;
            }
            catch { existingInstall = null; }
            if (existingInstall == null) return;

            if (existingInstall.Edition == EditionChoice.Msp) RadModeMsp.IsChecked = true; else RadModeSingle.IsChecked = true;
            if (existingInstall.Engine == DbEngine.Postgres)
            {
                RadDbPostgres.IsChecked = true;
                if (!string.IsNullOrWhiteSpace(existingInstall.ConnectionString)) TxtPgString.Text = existingInstall.ConnectionString;
            }
            else if (!string.IsNullOrWhiteSpace(existingInstall.ConnectionString))
            {
                ChkInstallSql.IsChecked = false;
                TxtSqlString.Text = existingInstall.ConnectionString;
            }
            Log($"Existing install found: {existingInstall.Edition} on {existingInstall.Engine}. Its settings are preselected.");
            RefreshDatabasePanels();
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

            if (SelectedEngine == DbEngine.Postgres)
            {
                try
                {
                    var pg = new Npgsql.NpgsqlConnectionStringBuilder(TxtPgString.Text.Trim());
                    if (string.IsNullOrWhiteSpace(pg.Host) || string.IsNullOrWhiteSpace(pg.Database) || string.IsNullOrWhiteSpace(pg.Username))
                    {
                        MessageBox.Show("The PostgreSQL connection string needs Host, Database and Username (and usually Password).");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("That PostgreSQL connection string isn't valid:" + Environment.NewLine + Environment.NewLine + ex.Message);
                    return;
                }
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
            plannedEdition = SelectedEdition;
            plannedEngine = SelectedEngine;
            installSqlServer = plannedEngine == DbEngine.SqlServer && ChkInstallSql.IsChecked == true;
            existingSqlConnectionString = plannedEngine == DbEngine.Postgres ? TxtPgString.Text.Trim() : TxtSqlString.Text;
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
        private EditionChoice plannedEdition = EditionChoice.Single;
        private DbEngine plannedEngine = DbEngine.SqlServer;
        private bool installSqlServer;
        private string existingSqlConnectionString = "";
        private bool plannedLocalOnly;
        private Uri plannedUri = new("http://localhost:8080");
        private string plannedAdminEmail = "";
        private string plannedTenant = "";

        // The step the run is on, so a failure is explained by where it happened.
        private InstallStage stage;

        private async Task RunInstallationAsync()
        {
            try
            {
                // Ensure Azure login first
                stage = InstallStage.SignIn;
                UpdateProgress(10, $"Signing in to {plannedTenant}...");
                Log($"Looking up the Microsoft 365 tenant '{plannedTenant}'...");
                tenantId = await ResolveTenantIdAsync(plannedTenant);
                var tenantForLogin = plannedTenant;
                await Task.Run(() => EnsureAzureLogin(tenantForLogin, tenantId));

                // MSP mode refuses SQL Express before anything is installed: an MSP
                // install outgrows it within a few dozen clients (InstallPlan).
                if (plannedEdition == EditionChoice.Msp && plannedEngine == DbEngine.SqlServer)
                {
                    stage = InstallStage.SqlServer;
                    var edition = installSqlServer ? null : await Task.Run(() => DatabaseSetup.SqlEngineEdition(existingSqlConnectionString));
                    stage = InstallStage.DatabaseChoice;
                    var problem = InstallPlan.DatabaseProblem(plannedEdition, plannedEngine, installSqlServer, edition);
                    if (problem != null) throw new Exception(problem);
                }

                // Database setup
                stage = plannedEngine == DbEngine.Postgres ? InstallStage.Postgres : InstallStage.SqlServer;
                if (plannedEngine == DbEngine.Postgres)
                {
                    sqlConnectionString = existingSqlConnectionString;
                    UpdateProgress(30, "Preparing the PostgreSQL database...");
                    try
                    {
                        var pg = sqlConnectionString;
                        await Task.Run(() => DatabaseSetup.PreparePostgres(pg, Log));
                    }
                    catch (Exception ex)
                    {
                        throw new Exception("Could not prepare the PostgreSQL database for Vigil365.\r\n\r\n" + ex.Message, ex);
                    }
                }
                else
                {
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
                }

                // App Registration. Must be the canonical origin, not the raw text:
                // Entra matches redirect URIs by exact string.
                stage = InstallStage.AppRegistration;
                UpdateProgress(40, "Creating Azure App Registration...");
                var uri = plannedUri;
                var origin = uri.IsDefaultPort
                    ? $"{uri.Scheme}://{uri.Host}"
                    : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
                await Task.Run(() => RegisterAzureApp(origin));

                // Application files
                stage = InstallStage.Files;
                UpdateProgress(60, "Installing application files...");
                await InstallApplicationFiles();

                // Service Setup. The collector secret is minted only now, with the
                // files in place: minting it earlier meant a run that failed on a
                // locked file left a live two-year secret on the app that nothing
                // had been configured with.
                stage = InstallStage.Service;
                UpdateProgress(80, "Configuring Windows Service...");
                await Task.Run(CreateCollectorSecret);
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
            var (title, remedy) = InstallPlan.FailureAdvice(stage);

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
        /// Signs the Azure CLI in interactively, in a window the user can see.
        ///
        /// This deliberately does not go through RunCommand: that hides the
        /// console and redirects its streams, which is right for every
        /// non-interactive command and fatal for this one. az login opens a
        /// browser and may fall back to printing a device code — with the window
        /// hidden there is nothing to read and nothing to answer, so it blocks
        /// indefinitely.
        /// </summary>
        private void RunAzLogin(string tenant)
        {
            Log("A sign-in window has opened. Sign in there with an account in this tenant, then installation continues.");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c az login --tenant {tenant} --allow-no-subscriptions",
                UseShellExecute = false,
                CreateNoWindow = false,   // interactive: the user must be able to see and answer it
            };

            using var p = Process.Start(psi)
                ?? throw new Exception("Could not start the Azure CLI to sign in.");
            p.WaitForExit();

            if (p.ExitCode != 0)
                throw new Exception(
                    $"Azure sign-in did not complete (exit code {p.ExitCode}).\r\n\r\n" +
                    $"Sign in manually and then run the installation again:\r\n\r\n" +
                    $"    az login --tenant {tenant} --allow-no-subscriptions");
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
            Log($"Signing in to {tenant} — a browser window will open...");
            
            // Clear any existing session to ensure we always start a fresh one
            try { RunCommandAndCapture("az", "account clear"); } catch { }

            // --allow-no-subscriptions because a Microsoft 365 tenant frequently
            // has no Azure subscription, and without it the CLI refuses to
            // complete a sign-in that is otherwise perfectly valid.
            //
            // Run in a VISIBLE console. RunCommand hides the window, and az login
            // is interactive — it prints a device code or waits on a prompt — so
            // hidden it simply blocks forever with the wizard stuck on "Signing
            // in…" and nothing to click.
            RunAzLogin(tenant);

            string current;
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
            //
            // Which one: the app this install's own config names. A single app named
            // exactly "Vigil365" that the config does not name is only reused if the
            // operator says it is this server's: it may be another server's, and
            // reusing it adds this server's secret to it and rewrites its settings.
            // It used to be taken silently — a Single pilot beside an MSP server
            // then made the MSP's app single-tenant, and every client's collection
            // failed.
            string? objectId = null;
            var reused = false;
            string? existingJson = null;
            var appName = "Vigil365";
            if (existingInstall?.ClientId is string configuredId)
            {
                var (found, appJson, _) = RunCommandChecked("az", $"ad app show --id {configuredId} -o json");
                if (found) existingJson = appJson;
                else Log($"The app registration this install used ({configuredId}) is not in this tenant; looking for another.");
            }
            if (existingJson == null)
            {
                var named = InstallPlan.AppIdsNamedExactly(
                    RunCommandAndCapture("az", "ad app list --display-name \"Vigil365\" -o json"), "Vigil365");
                if (named.Count > 1)
                    throw new Exception(
                        $"Several app registrations in this tenant are named Vigil365 ({string.Join(", ", named)}), " +
                        "and this server has no previous configuration saying which one is its own.\r\n\r\n" +
                        "Rename or delete the ones this server should not use, then run the installer again.");
                if (named.Count == 1)
                {
                    var hostName = InstallPlan.HostAppName(Environment.MachineName);
                    var answer = Dispatcher.Invoke(() => MessageBox.Show(this,
                        $"This tenant already has an app registration named Vigil365 ({named[0]}), " +
                        "and this server has no previous configuration naming it. It may belong to another Vigil365 server.\r\n\r\n" +
                        "Sharing it adds this server's address and a collection secret to it, and in MSP mode makes it multi-tenant.\r\n\r\n" +
                        "Yes: share it (choose this only if it is this server's own registration, e.g. a reinstall).\r\n" +
                        $"No: create a separate registration named \"{hostName}\" for this server.\r\n" +
                        "Cancel: stop the installation.",
                        "Existing Vigil365 app registration", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No));
                    if (answer == MessageBoxResult.Cancel)
                        throw new Exception("Installation stopped: choose whether to share the existing Vigil365 app registration, then run the installer again.");
                    if (answer == MessageBoxResult.Yes)
                    {
                        var (found, appJson, _) = RunCommandChecked("az", $"ad app show --id {named[0]} -o json");
                        if (found) existingJson = appJson;
                    }
                    else appName = hostName;
                }
            }

            // Kept, not replaced: another install may share this registration.
            IReadOnlyList<string> spaRedirects = Array.Empty<string>(), webRedirects = Array.Empty<string>();
            string? currentAudience = null;
            if (existingJson != null)
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<JsonElement>(existingJson);
                    clientId = existing.GetProperty("appId").GetString();
                    objectId = existing.GetProperty("id").GetString();
                    if (existing.TryGetProperty("displayName", out var dn) && dn.GetString() is string name) appName = name;
                    if (existing.TryGetProperty("signInAudience", out var aud)) currentAudience = aud.GetString();
                    spaRedirects = InstallPlan.RedirectUris(existingJson, "spa");
                    webRedirects = InstallPlan.RedirectUris(existingJson, "web");
                    reused = true;
                    Log($"Reusing the existing app registration \"{appName}\" ({clientId}).");
                }
                catch { /* fall through to creating one */ }
            }

            if (objectId == null)
            {
                Log($"Creating Entra Application \"{appName}\"...");
                var appJson = RunCommandAndCapture("az", $"ad app create --display-name \"{appName}\" --sign-in-audience AzureADMyOrg");

                var app = JsonSerializer.Deserialize<JsonElement>(appJson);
                clientId = app.GetProperty("appId").GetString();
                objectId = app.GetProperty("id").GetString();

                Log($"Created App Registration. Client ID: {clientId}");
            }

            // Decide whether to (re)define the exposed access_as_user scope. Entra
            // rejects a PATCH that replaces an already-enabled scope with
            // CannotDeleteOrUpdateEnabledEntitlement, and our scope carries a fresh
            // GUID each run — so on a reused app that already exposes the scope we
            // must leave it untouched. Only define it when it is genuinely absent.
            var defineScope = true;
            if (reused)
            {
                try
                {
                    var scopesJson = RunCommandAndCapture("az",
                        $"ad app show --id {clientId} --query \"api.oauth2PermissionScopes[].value\" -o json");
                    if (scopesJson.Contains("access_as_user", StringComparison.OrdinalIgnoreCase))
                    {
                        defineScope = false;
                        Log("   access_as_user scope already present — leaving it as-is.");
                    }
                }
                catch { /* if we cannot tell, fall back to defining it */ }
            }

            // Graph application permissions. Without these the install completes,
            // people can sign in, and every collector then fails on authorization
            // — the dashboard is simply empty with no indication why.
            Log("Resolving Microsoft Graph permissions for this tenant...");
            var appRolesJson = RunCommandAndCapture("az",
                $"ad sp show --id {GraphPermissions.GraphAppId} --query appRoles -o json");

            var wanted = GraphPermissions.Required.Concat(GraphPermissions.Optional);
            var (requiredResourceAccess, missing) = GraphPermissions.BuildRequiredResourceAccess(appRolesJson, wanted);
            foreach (var name in missing)
                Log($"   note: this tenant does not offer '{name}' — skipped.");
            Log($"Requesting {GraphPermissions.Required.Length} Graph permissions.");

            // The exposed scope is included only when it needs defining (new app,
            // or a reused app that somehow lacks it). Re-sending it on an app that
            // already exposes it is what triggers CannotDeleteOrUpdateEnabledEntitlement.
            var apiBlock = defineScope
                ? $$"""
                ,
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
                """
                : "";

            // Edition-aware (InstallPlan.AppPatchJson): MSP mode makes the app
            // multi-tenant and registers the /consented Web redirect so client
            // tenants can consent. On a re-run over a Single install this patches
            // the SAME app — the convert-to-MSP path, no new app or secret.
            var patchJson = InstallPlan.AppPatchJson(plannedEdition, publicUrl, requiredResourceAccess, apiBlock, spaRedirects, webRedirects, currentAudience);
            Log(plannedEdition == EditionChoice.Msp
                ? $"MSP mode: multi-tenant app, client consent returns to {InstallPlan.ConsentRedirect(publicUrl)}."
                : InstallPlan.SignInAudience(plannedEdition, currentAudience) == "AzureADMultipleOrgs"
                    ? "Single-organisation mode. The app is already multi-tenant (another install may rely on that), so it stays so; sign-in is still limited to your tenant."
                    : "Single-organisation mode: single-tenant app.");

            var tempPatch = Path.GetTempFileName();
            File.WriteAllText(tempPatch, patchJson);
            
            Log("Configuring redirect URI, exposed scope and Graph permissions...");
            var (patchOk, _, patchErr) = RunCommandChecked("az",
                $"rest --method PATCH --uri \"https://graph.microsoft.com/v1.0/applications/{objectId}\" " +
                $"--headers \"Content-Type=application/json\" --body \"@{tempPatch}\"");
            File.Delete(tempPatch);

            // If this fails the app has no Graph permissions and no SPA redirect
            // URI, so neither collection nor sign-in can work. That is not a
            // warning to note and move past — stop, so the failure UI explains it
            // rather than the browser doing so later.
            if (!patchOk)
                throw new Exception(
                    "Could not configure the Vigil365 app registration (redirect URI, scope and Graph permissions).\r\n\r\n" +
                    patchErr);

            // Idempotent — the service principal may already exist if the app is
            // being reused, and that is not an error.
            RunCommandChecked("az", $"ad sp create --id {clientId}");

            // Admin consent routinely fails on the first try: the permissions and
            // service principal we just created take a few seconds to replicate
            // across Entra, and consent against a not-yet-visible SP returns an
            // error. Retry with a short backoff before giving up, and only report
            // success when az actually succeeds — RunCommand's old fire-and-forget
            // reported consent as granted even when it was not.
            Log("Granting admin consent for the Graph permissions...");
            var consented = false;
            string lastConsentError = "";
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var (ok, _, stderr) = RunCommandChecked("az", $"ad app permission admin-consent --id {clientId}");
                if (ok) { consented = true; break; }
                lastConsentError = stderr;
                if (attempt < 5) { Log($"   consent not ready yet (attempt {attempt}/5), retrying..."); System.Threading.Thread.Sleep(6000); }
            }

            if (consented)
            {
                Log("Admin consent granted.");
            }
            else
            {
                Log($"WARNING: Could not grant admin consent automatically. {lastConsentError}");
                Log("   The install continues, but collection stays empty until consent is granted:");
                Log($"   Entra admin center > App registrations > {appName} > API permissions > Grant admin consent.");
            }

            if (plannedEdition == EditionChoice.Msp)
                GrantMspReadinessPermission(appRolesJson);
        }

        /// <summary>
        /// The collector authenticates to Graph app-only, so it needs a
        /// credential of its own — the user's sign-in cannot be reused. Nothing
        /// created one before, so collection could never start and the Setup
        /// page demanded a secret the operator had to mint by hand.
        ///
        /// --append so an existing credential someone else added is not
        /// destroyed; the trade-off is that re-running the wizard leaves the
        /// previous installer secret behind, unused, until it expires.
        /// </summary>
        private void CreateCollectorSecret()
        {
            Log("Creating a client secret for data collection...");
            // Stdout carries only the password (--query password -o tsv); az prints
            // its "protect these credentials" notice to stderr, kept separate so it
            // cannot contaminate the secret. Checking the exit code means a failure
            // is a failure, not an empty string quietly written into the config.
            var (secretOk, secretOut, secretErr) = RunCommandChecked("az",
                $"ad app credential reset --id {clientId} --append --years 2 " +
                $"--display-name \"Vigil365 collector\" --query password -o tsv");

            if (secretOk && !string.IsNullOrWhiteSpace(secretOut))
            {
                graphClientSecret = secretOut;
                Log("Client secret created and stored in the Vigil365 configuration.");
            }
            else
            {
                Log($"WARNING: Could not create a client secret. {secretErr}");
                Log("   Collection stays disabled until a secret is added on the Setup page in the browser.");
            }
        }

        /// <summary>
        /// MSP mode: let the app read its OWN registration (Application.Read.All) so
        /// the onboarding dialog can say whether client consent will work. Granted
        /// as a direct app-role assignment in the MSP's tenant — deliberately not in
        /// requiredResourceAccess, so clients are never asked to consent to it.
        /// Best effort: without it the readiness card just shows "not checked".
        /// </summary>
        private void GrantMspReadinessPermission(string appRolesJson)
        {
            try
            {
                var roleId = InstallPlan.AppRoleId(appRolesJson, "Application.Read.All");
                var ourSp = RunCommandAndCapture("az", $"ad sp show --id {clientId} --query id -o tsv").Trim();
                var graphSp = RunCommandAndCapture("az", $"ad sp show --id {GraphPermissions.GraphAppId} --query id -o tsv").Trim();
                if (string.IsNullOrEmpty(roleId) || string.IsNullOrEmpty(ourSp) || string.IsNullOrEmpty(graphSp))
                {
                    Log("   note: could not resolve Application.Read.All — the MSP app readiness card will show 'not checked'.");
                    return;
                }
                var body = Path.GetTempFileName();
                File.WriteAllText(body, $$"""{"principalId":"{{ourSp}}","resourceId":"{{graphSp}}","appRoleId":"{{roleId}}"}""");
                var (ok, _, err) = RunCommandChecked("az",
                    $"rest --method POST --uri \"https://graph.microsoft.com/v1.0/servicePrincipals/{graphSp}/appRoleAssignedTo\" " +
                    $"--headers \"Content-Type=application/json\" --body \"@{body}\"");
                File.Delete(body);
                if (ok || err.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                    Log("Granted Application.Read.All in your own tenant (for the MSP app readiness check; clients never see it).");
                else
                    Log($"   note: could not grant Application.Read.All ({err.Trim()}). The readiness card will show 'not checked'.");
            }
            catch (Exception ex)
            {
                Log($"   note: could not grant Application.Read.All ({ex.Message}).");
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
            var before = InstallPlan.ParseServiceState(QueryService("Vigil365"));
            RunCommand("sc", "stop Vigil365");

            try
            {
                await WaitForServiceStoppedAsync("Vigil365", TimeSpan.FromSeconds(60));

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
            }
            catch
            {
                // It did not stop in time, or the files could not be replaced. A clean
                // stop is not restarted by the service's recovery actions, so without
                // this a failed upgrade left monitoring down until someone noticed.
                if (InstallPlan.RestartAfterFailedUpgrade(before))
                {
                    Log("Starting the previous Vigil365 service again...");
                    RunCommand("sc", "start Vigil365");
                }
                throw;
            }

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
            // Logs too: the default logs\ beside the app is under Program Files,
            // where the service cannot create it.
            var logDir = Path.Combine(dataDir, "logs");

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

            // Serialize the free-form values through System.Text.Json rather than
            // hand-escaping. A client secret or connection string can in principle
            // contain any character, and JsonSerializer emits a complete, quoted,
            // fully-escaped JSON string — so these are interpolated WITHOUT
            // surrounding quotes.
            var connJson = JsonSerializer.Serialize(sqlConnectionString);

            // If no new secret could be minted for the same app, keep the one the
            // previous config already holds rather than blanking a working install.
            var keepPreviousSecret = graphClientSecret.Length == 0
                && string.Equals(existingInstall?.ClientId, clientId, StringComparison.OrdinalIgnoreCase);
            var secretLine = keepPreviousSecret ? "" : $",\n        \"ClientSecret\": {JsonSerializer.Serialize(graphClientSecret)}";

            var installerJson = $$"""
            {
            {{kestrelJson}}
            {{InstallPlan.ConfigSections(plannedEdition, plannedEngine)}}
                "ConnectionStrings": {
                    "DefaultConnection": {{connJson}}
                },
                "AzureAd": {
                    "Instance": "https://login.microsoftonline.com/",
                    "TenantId": "{{tenantId}}",
                    "ClientId": "{{clientId}}",
                    "Audience": "api://{{clientId}}"
                },
                "Graph": {
                    "TenantId": "{{tenantId}}",
                    "ClientId": "{{clientId}}"{{secretLine}}
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
                },
                "Logging": {
                    "File": {
                        "Path": {{JsonSerializer.Serialize(Path.Combine(logDir, "vigil365-.json"))}}
                    }
                }
            }
            """;

            // Laid over the previous file, not written in its place: settings
            // Setup does not manage (Retention, Graph tuning, the size warning)
            // survive a re-run. The previous file is kept beside it as .bak.
            var configPath = Path.Combine(publishPath, "appsettings.Production.json");
            var previousConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
            if (previousConfig != null)
            {
                WriteRestrictedFile(configPath + ".bak", previousConfig);
                Log("Keeping the settings Setup does not manage; the previous configuration is saved as appsettings.Production.json.bak.");
            }

            Log("Writing Configuration File...");
            WriteRestrictedFile(configPath, InstallPlan.MergeConfig(previousConfig, installerJson));

            const string serviceAccount = "NT AUTHORITY\\LOCAL SERVICE";

            // The key ring decrypts every secret Vigil365 keeps in its database, and
            // the logs name users and devices: the service may write here, and no
            // one else but administrators may read. ProgramData otherwise lets every
            // local user read what is under it.
            Directory.CreateDirectory(keyPath);
            Directory.CreateDirectory(logDir);
            try
            {
                RestrictAccess(new DirectoryInfo(dataDir), InstallPlan.DataFolderAcl());
                Log($"Data protection keys will be stored in {keyPath}, logs in {logDir}.");
            }
            catch (Exception ex)
            {
                Log($"Could not set permissions on {dataDir} ({ex.Message}). The service may be unable to save its keys or logs.");
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
        /// Writes a file only administrators, SYSTEM and the service account can
        /// read (<see cref="InstallPlan.ConfigFileAcl"/>). The ACL is set before the
        /// content goes in, so the secret is never readable by all.
        /// </summary>
        private static void WriteRestrictedFile(string path, string contents)
        {
            using (File.Open(path, FileMode.OpenOrCreate)) { }
            RestrictAccess(new FileInfo(path), InstallPlan.ConfigFileAcl());
            File.WriteAllText(path, contents);
        }

        /// <summary>
        /// Replaces the target's ACL with <paramref name="plan"/> — who gets what is
        /// decided (and unit-tested) in InstallPlan; this only applies it.
        /// </summary>
        private static void RestrictAccess(FileSystemInfo target, RestrictedAcl plan)
        {
            static FileSystemRights Rights(AclRights r) => r switch
            {
                AclRights.FullControl => FileSystemRights.FullControl,
                AclRights.Modify => FileSystemRights.Modify,
                _ => FileSystemRights.Read,
            };
            var inheritance = plan.InheritedByChildren
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;

            FileSystemSecurity acl = target is DirectoryInfo ? new DirectorySecurity() : new FileSecurity();
            acl.SetAccessRuleProtection(isProtected: plan.ProtectFromParent, preserveInheritance: false);
            foreach (var grant in plan.Grants)
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(grant.Sid), Rights(grant.Rights),
                    inheritance, PropagationFlags.None, AccessControlType.Allow));

            if (target is DirectoryInfo dir) dir.SetAccessControl((DirectorySecurity)acl);
            else ((FileInfo)target).SetAccessControl((FileSecurity)acl);
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
                var text = QueryService(serviceName);

                if (text.Contains("RUNNING")) { Log($"Service '{serviceName}' is running."); return; }
                if (text.Contains("STOPPED") && attempt > 2) break;
                System.Threading.Thread.Sleep(1000);
            }

            throw new Exception(
                $"The '{serviceName}' service was installed but is not running. " +
                "It usually means it could not reach its database, could not read its certificate, " +
                "or the port is already in use. Windows Event Viewer > Windows Logs > Application " +
                "records the reason.");
        }

        /// <summary>
        /// Waits until the service has actually stopped, or does not exist. "sc stop"
        /// only asks: the service then finishes its collection cycle and drains its
        /// hosted services (up to 30 seconds) while still holding its files, so
        /// overwriting them after a fixed pause failed on a locked file.
        /// </summary>
        private async Task WaitForServiceStoppedAsync(string serviceName, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (InstallPlan.FilesReplaceable(InstallPlan.ParseServiceState(QueryService(serviceName)))) return;
                if (DateTime.UtcNow >= deadline)
                    throw new Exception(
                        $"The '{serviceName}' service did not stop within {timeout.TotalSeconds:0} seconds, so its files " +
                        "could not be replaced. Nothing was replaced. Stop it in Services, then run the installer again.");
                await Task.Delay(1000);
            }
        }

        /// <summary>The output of "sc query" for the service (an error text when it does not exist).</summary>
        private static string QueryService(string serviceName)
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
            return text;
        }

        private void BtnNextToDone_Click(object sender, RoutedEventArgs e)
        {
            TxtDoneAddress.Text = $"Vigil365 is available at {installedUrl}";
            TxtDoneNextSteps.Text = "Next:" + Environment.NewLine + string.Join(Environment.NewLine, InstallPlan.NextSteps(plannedEdition, plannedAdminEmail).Select((s, i) => $"{i + 1}. {s}"));

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

        /// <summary>
        /// Runs a command and reports whether it actually succeeded, with stdout
        /// and stderr captured together for the log. RunCommand discards the exit
        /// code, so callers that branched on its "success" were really branching
        /// on "it ran at all" — which is how a failed admin-consent still reported
        /// as granted.
        /// </summary>
        private (bool Ok, string Stdout, string Stderr) RunCommandChecked(string fileName, string arguments)
        {
            var p = new Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = $"/c {fileName} {arguments}";
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;

            p.Start();
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode == 0, stdout.Trim(), stderr.Trim());
        }
    }
}