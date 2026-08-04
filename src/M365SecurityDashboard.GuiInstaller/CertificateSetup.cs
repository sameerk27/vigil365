using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace M365SecurityDashboard.GuiInstaller
{
    /// <summary>
    /// A certificate already present in LocalMachine\My, as offered in the wizard.
    /// </summary>
    internal sealed class StoreCertificate
    {
        public string Subject { get; set; } = "";
        public string Thumbprint { get; set; } = "";
        public string Display { get; set; } = "";
    }

    /// <summary>
    /// The Kestrel "Certificate" node to write into appsettings.Production.json,
    /// plus a line for the install log.
    /// </summary>
    internal sealed class CertificateBinding
    {
        public string Json { get; set; } = "";
        public string Description { get; set; } = "";

        /// <summary>Set only for store-based certificates, where the private key needs an ACL grant.</summary>
        public string? Thumbprint { get; set; }

        /// <summary>Set only for file-based certificates, which the service account must be able to read.</summary>
        public string? PfxPath { get; set; }
    }

    /// <summary>
    /// Certificate acquisition for the installer.
    ///
    /// Sign-in goes through Entra, and Entra refuses plain HTTP redirect URIs for
    /// anything except localhost. So an install that is reachable by hostname has
    /// to serve HTTPS or sign-in simply cannot work — a certificate is not an
    /// optional extra here, which is why the wizard always ends up with one.
    ///
    /// The three sources map onto how organisations actually hold certificates:
    /// most already have one issued by an internal CA (store), some keep a
    /// wildcard as a file (pfx), and anyone with neither needs something that
    /// works today without learning ACME (self-signed).
    /// </summary>
    internal static class CertificateSetup
    {
        private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

        /// <summary>
        /// Certificates in LocalMachine\My that could plausibly serve a site:
        /// usable private key, not expired, and either a server-auth EKU or no
        /// EKU at all (an absent EKU means "no restriction").
        /// </summary>
        public static List<StoreCertificate> ListUsable(string hostname)
        {
            var result = new List<StoreCertificate>();
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            try { store.Open(OpenFlags.ReadOnly); }
            catch { return result; }

            foreach (var cert in store.Certificates)
            {
                if (!cert.HasPrivateKey) continue;
                if (cert.NotAfter < DateTime.Now) continue;

                var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
                if (eku != null && !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthOid))
                    continue;

                var cn = CommonName(cert.Subject);
                // Flagging the match is worth more than filtering on it: wildcard
                // and SAN certs are common, and silently hiding a certificate the
                // admin knows is correct is worse than showing an extra one.
                var matches = !string.IsNullOrWhiteSpace(hostname) && Matches(cert, hostname);
                result.Add(new StoreCertificate
                {
                    Subject = cn,
                    Thumbprint = cert.Thumbprint ?? "",
                    Display = $"{(matches ? "✓ " : "")}{cn}  —  expires {cert.NotAfter:yyyy-MM-dd}"
                });
            }

            store.Close();
            return result.OrderByDescending(c => c.Display.StartsWith("✓")).ThenBy(c => c.Subject).ToList();
        }

        private static bool Matches(X509Certificate2 cert, string hostname)
        {
            bool Match(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate)) return false;
                candidate = candidate.Trim();
                if (candidate.StartsWith("*."))
                    return hostname.EndsWith(candidate[1..], StringComparison.OrdinalIgnoreCase)
                           && hostname.Count(c => c == '.') == candidate.Count(c => c == '.');
                return string.Equals(candidate, hostname, StringComparison.OrdinalIgnoreCase);
            }

            if (Match(CommonName(cert.Subject))) return true;

            // SAN is the field browsers actually honour; CN alone has been
            // ignored by Chrome since 58.
            var san = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
            if (san == null) return false;
            var text = san.Format(false);
            return text.Split(',')
                       .Select(p => p.Contains('=') ? p[(p.IndexOf('=') + 1)..] : p)
                       .Any(Match);
        }

        private static string CommonName(string subject)
        {
            foreach (var part in subject.Split(','))
            {
                var t = part.Trim();
                if (t.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return t[3..];
            }
            return subject;
        }

        /// <summary>
        /// Generates a self-signed certificate for <paramref name="hostname"/>,
        /// writes it beside the application, and trusts it on THIS machine.
        ///
        /// Trusting it locally is what stops the server's own browser warning.
        /// It does nothing for anyone else — every other machine still warns,
        /// which on a security product teaches people to click through TLS
        /// warnings. That is why the wizard labels this a starting point.
        /// </summary>
        public static CertificateBinding CreateSelfSigned(string hostname, string installDir, Action<string> log)
        {
            var pfxPath = Path.Combine(installDir, "vigil365-selfsigned.pfx");
            var password = Guid.NewGuid().ToString("N");

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(hostname);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(ServerAuthOid) }, false));

            // Backdated a day so a server whose clock is slightly behind does not
            // reject a certificate that was valid the moment it was created.
            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));

            Directory.CreateDirectory(installDir);
            File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, password));
            log($"Generated a certificate for {hostname} (valid until {cert.NotAfter:yyyy-MM-dd}).");

            try
            {
                // Public half only. The Root store exists to answer "do I trust
                // this identity"; the private key has no business being there.
                using var publicOnly = new X509Certificate2(cert.Export(X509ContentType.Cert));
                using var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                root.Open(OpenFlags.ReadWrite);
                root.Add(publicOnly);
                root.Close();
                log("Trusted it on this server, so this machine's browser will not warn.");
            }
            catch (Exception ex)
            {
                log($"Could not add it to the trusted store ({ex.Message}). The site still works; this machine will show a warning.");
            }

            return new CertificateBinding
            {
                Json = $$"""
                            "Path": "{{Path.GetFileName(pfxPath)}}",
                            "Password": "{{password}}"
                """,
                Description = $"self-signed certificate for {hostname}",
                PfxPath = pfxPath
            };
        }

        /// <summary>Points Kestrel at a certificate the admin supplied as a file.</summary>
        public static CertificateBinding FromPfx(string pfxPath, string password)
        {
            if (!File.Exists(pfxPath))
                throw new FileNotFoundException($"Certificate file not found: {pfxPath}");

            // Fail here rather than after the service is registered and refuses
            // to start with nothing but an event-log entry to show for it.
            using var probe = new X509Certificate2(pfxPath, password);
            if (!probe.HasPrivateKey)
                throw new InvalidOperationException("That .pfx has no private key, so it cannot be used to serve HTTPS.");

            return new CertificateBinding
            {
                Json = $$"""
                            "Path": "{{pfxPath.Replace("\\", "\\\\")}}",
                            "Password": "{{password.Replace("\\", "\\\\").Replace("\"", "\\\"")}}"
                """,
                Description = $"{CommonName(probe.Subject)} (from file, expires {probe.NotAfter:yyyy-MM-dd})",
                PfxPath = pfxPath
            };
        }

        /// <summary>
        /// Points Kestrel at a certificate already in LocalMachine\My. Kestrel
        /// resolves these by subject, not thumbprint, so the subject is what gets
        /// written.
        /// </summary>
        public static CertificateBinding FromStore(StoreCertificate chosen)
        {
            return new CertificateBinding
            {
                Json = $$"""
                            "Subject": "{{chosen.Subject}}",
                            "Store": "My",
                            "Location": "LocalMachine",
                            "AllowInvalid": false
                """,
                Description = $"{chosen.Subject} (from the Windows certificate store)",
                Thumbprint = chosen.Thumbprint
            };
        }

        /// <summary>
        /// Lets the service account read the certificate's private key.
        ///
        /// Windows stores private keys as files with their own ACL, separate from
        /// the certificate. An admin importing a certificate grants themselves
        /// access, not LOCAL SERVICE — so without this the service installs
        /// cleanly, then fails to start with nothing but a Kestrel error in the
        /// event log. This is the single most common way a working certificate
        /// still produces a dead site.
        /// </summary>
        public static void GrantKeyAccess(string thumbprint, string account, Action<string> log)
        {
            try
            {
                using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                var cert = store.Certificates.Cast<X509Certificate2>()
                                .FirstOrDefault(c => c.Thumbprint == thumbprint);
                store.Close();
                if (cert == null) return;

                string? keyFile = null;
                using (var rsa = cert.GetRSAPrivateKey())
                {
                    if (rsa is System.Security.Cryptography.RSACng cng)
                    {
                        keyFile = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            "Microsoft", "Crypto", "Keys", cng.Key.UniqueName ?? "");
                    }
                    else if (rsa is System.Security.Cryptography.RSACryptoServiceProvider csp)
                    {
                        keyFile = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            "Microsoft", "Crypto", "RSA", "MachineKeys",
                            csp.CspKeyContainerInfo.UniqueKeyContainerName);
                    }
                }

                if (keyFile == null || !File.Exists(keyFile))
                {
                    log($"Could not locate the private key file for {cert.Subject}; if the service fails to start, grant '{account}' read access to it manually.");
                    return;
                }

                GrantRead(keyFile, account);
                log($"Granted {account} read access to the certificate's private key.");
            }
            catch (Exception ex)
            {
                log($"Could not grant private-key access ({ex.Message}). If the service fails to start, this is why.");
            }
        }

        /// <summary>Adds a read ACE for <paramref name="account"/> without disturbing existing rights.</summary>
        public static void GrantRead(string path, string account)
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                account,
                System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }
}
