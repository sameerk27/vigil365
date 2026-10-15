#!/usr/bin/env bash
# What enterprise-install.sh writes to appsettings.Production.json must be valid
# JSON that gives back every value as entered. A SQL named instance
# (Server=sql01\PROD) written raw was an invalid \P escape, and the service could
# not load its config. Installs nothing: sourced, the script returns right after
# defining its functions. Run in CI: bash scripts/tests/enterprise-install.test.sh
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=../../enterprise-install.sh
source "$repo/enterprise-install.sh"

py=
for c in python3 python; do
  if command -v "$c" >/dev/null 2>&1 && "$c" -c '' >/dev/null 2>&1; then py=$c; break; fi
done
[[ -n $py ]] || { echo "python3 is required to validate the JSON" >&2; exit 1; }

failures=0
t() { # description, command...
  local name="$1"; shift
  if "$@"; then echo "ok   - $name"; else echo "FAIL - $name"; failures=$((failures + 1)); fi
}

# json() must produce one JSON string that decodes to exactly its input.
round_trips() {
  json "$1" | EXPECTED="$1" "$py" -c '
import json, os, sys
v = json.loads(sys.stdin.read())
sys.exit(0 if v == os.environ["EXPECTED"] else "decoded to %r" % v)'
}

t "a named instance's backslash"            round_trips 'Server=sql01\PROD;Database=Vigil365;Trusted_Connection=True'
t "quotes and a backslash before a quote"   round_trips 'Password=p"a\"ss;Server=.\SQLEXPRESS'
t "tab, newline and carriage return"        round_trips $'a\tb\nc\rd'
t "a plain value"                           round_trips 'Host=db;Database=vigil365;Username=vigil'

# The whole file, as the installer writes it for an MSP on a named SQL instance.
settings_are_valid() {
  mode=Msp db_provider=SqlServer
  sql_connection='Server=sql01\PROD;Database=Vigil365;User Id=vigil;Password=p"w\d'
  tenant_id=11111111-1111-1111-1111-111111111111 client_id=22222222-2222-2222-2222-222222222222
  admin_email=admin@contoso.com public_url=https://vigil365.contoso.com/ install_dir=/opt/vigil365
  settings_json | CONN="$sql_connection" "$py" -c '
import json, os, sys
s = json.load(sys.stdin)
problems = []
def expect(path, actual, wanted):
    if actual != wanted: problems.append("%s: %r != %r" % (path, actual, wanted))
expect("ConnectionStrings.DefaultConnection", s["ConnectionStrings"]["DefaultConnection"], os.environ["CONN"])
expect("Edition.Mode", s["Edition"]["Mode"], "Msp")
expect("Database.Provider", s["Database"]["Provider"], "SqlServer")
expect("AzureAd.TenantId", s["AzureAd"]["TenantId"], "11111111-1111-1111-1111-111111111111")
expect("AzureAd.Audience", s["AzureAd"]["Audience"], "api://22222222-2222-2222-2222-222222222222")
expect("Auth.BootstrapAdminEmail", s["Auth"]["BootstrapAdminEmail"], "admin@contoso.com")
expect("Cors.AllowedOrigins", s["Cors"]["AllowedOrigins"], ["https://vigil365.contoso.com"])
expect("DataProtection.KeyPath", s["DataProtection"]["KeyPath"], "/opt/vigil365/keys")
sys.exit("\n".join(problems) or 0)'
}
t "appsettings.Production.json for a named instance is valid JSON with every value intact" settings_are_valid

if (( failures > 0 )); then echo "$failures check(s) failed" >&2; exit 1; fi
echo "All enterprise-install.sh checks passed."
