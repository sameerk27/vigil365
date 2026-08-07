#!/usr/bin/env bash
# Installs Vigil365 as a managed Linux production service behind a TLS proxy.
set -euo pipefail

usage() { echo "Usage: sudo $0 [--tenant-id ID --client-id ID --admin-email EMAIL --sql-connection STRING --public-url https://host]"; }
tenant_id= client_id= admin_email= sql_connection= public_url= install_dir=/opt/vigil365 port=8080
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tenant-id) tenant_id="$2"; shift 2;; --client-id) client_id="$2"; shift 2;;
    --admin-email) admin_email="$2"; shift 2;; --sql-connection) sql_connection="$2"; shift 2;;
    --public-url) public_url="$2"; shift 2;; --install-dir) install_dir="$2"; shift 2;; --port) port="$2"; shift 2;;
    -h|--help) usage; exit 0;; *) usage; exit 2;;
  esac
done
[[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }
ask() { local label="$1" value="$2"; if [[ -n "$value" ]]; then printf '%s' "$value"; else read -r -p "$label: " value; [[ -n "$value" ]] || { echo "$label is required." >&2; exit 2; }; printf '%s' "$value"; fi; }
echo "Vigil365 enterprise installer"
tenant_id="$(ask 'Entra Tenant ID' "$tenant_id")"
client_id="$(ask 'Entra Application (client) ID' "$client_id")"
admin_email="$(ask 'First administrator email' "$admin_email")"
sql_connection="$(ask 'SQL Server connection string' "$sql_connection")"
public_url="$(ask 'Public HTTPS URL (for example https://vigil365.contoso.com)' "$public_url")"
[[ $public_url =~ ^https:// ]] || { echo "The public URL must start with https://" >&2; exit 2; }
command -v dotnet >/dev/null || { echo ".NET 8 SDK is required." >&2; exit 1; }
command -v npm >/dev/null || { echo "Node.js 20+ is required." >&2; exit 1; }
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

pushd "$repo_root/src/m365-security-dashboard-client" >/dev/null
npm ci --no-audit --no-fund
npm run build
popd >/dev/null
install -d -m 0750 -o root -g root "$install_dir"
dotnet publish "$repo_root/src/M365SecurityDashboard.Api" -c Release -o "$install_dir"
id -u vigil365 >/dev/null 2>&1 || useradd --system --home-dir "$install_dir" --shell /usr/sbin/nologin vigil365
chown -R root:vigil365 "$install_dir"
chmod -R go-rwx "$install_dir"
install -d -m 0750 -o vigil365 -g vigil365 "$install_dir/keys"
cat > "$install_dir/appsettings.Production.json" <<EOF
{"ConnectionStrings":{"DefaultConnection":"$sql_connection"},"AzureAd":{"Instance":"https://login.microsoftonline.com/","TenantId":"$tenant_id","ClientId":"$client_id","Audience":"api://$client_id"},"Auth":{"RedirectUri":"$public_url","BootstrapAdminEmail":"$admin_email"},"Cors":{"AllowedOrigins":["${public_url%/}"]},"Security":{"RequireHttps":false},"DataProtection":{"KeyPath":"$install_dir/keys"}}
EOF
chown root:vigil365 "$install_dir/appsettings.Production.json"
chmod 0640 "$install_dir/appsettings.Production.json"
cat > /etc/systemd/system/vigil365.service <<EOF
[Unit]
Description=Vigil365 Microsoft 365 security monitoring
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=vigil365
Group=vigil365
WorkingDirectory=$install_dir
ExecStart=$install_dir/M365SecurityDashboard.Api --environment Production --urls http://127.0.0.1:$port
Restart=on-failure
RestartSec=10
TimeoutStopSec=30
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=$install_dir/keys
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now vigil365
echo "Installed vigil365.service. Configure a TLS reverse proxy for $public_url -> http://127.0.0.1:$port, then add $public_url as an Entra SPA redirect URI."
