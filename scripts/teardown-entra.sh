#!/usr/bin/env bash
# Removes ONLY the demo's Entra objects created by setup-entra.sh: the "cost-to-serve-demo"
# app registration (with its service principal and role assignments) and the users
# dana.demo@<domain> and luis.demo@<domain>. Destructive: asks you to type 'delete'.
# Usage: ./scripts/teardown-entra.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
graph="https://graph.microsoft.com/v1.0"
domain="$(az rest --method GET --url "$graph/organization?\$select=verifiedDomains" \
  --query "value[0].verifiedDomains[?isDefault].name | [0]" -o tsv)"
app_id="$(az ad app list --display-name cost-to-serve-demo --query "[0].appId" -o tsv)"

echo "Will delete: app registration cost-to-serve-demo (${app_id:-not found}),"
echo "users dana.demo@$domain and luis.demo@$domain (if present)."
read -r -p "Type 'delete' to continue: " answer
[[ "$answer" == "delete" ]] || { echo "Cancelled. Nothing changed."; exit 1; }

[[ -z "$app_id" ]] || az ad app delete --id "$app_id"
for alias in dana luis; do
  oid="$(az ad user list --filter "userPrincipalName eq '$alias.demo@$domain'" --query "[0].id" -o tsv)"
  [[ -z "$oid" ]] || az ad user delete --id "$oid"
done

settings="$repo_root/src/CostToServe.Api/appsettings.Local.json"
if [[ -f "$settings" ]]; then
  python3 - "$settings" <<'PY'
import json, sys
data = json.load(open(sys.argv[1]))
data.pop("Entra", None)
with open(sys.argv[1], "w") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
PY
fi
rm -f "$repo_root/secrets/demo-users.local.txt"
echo "Done. Deleted objects stay in the directory's recycle bin for 30 days."
