#!/usr/bin/env bash
# Removes ONLY the demo's Entra objects created by setup-entra.sh: the "cost-to-serve-demo"
# app registration (with its service principal and role assignments) and the users
# dana.demo@<domain> and luis.demo@<domain>. Destructive: asks you to type 'delete'.
#
# Deleted objects stay in the directory's recycle bin for 30 days, and deleted users keep their
# sign-in names, so setup-entra.sh cannot recreate them meanwhile. --purge deletes them
# permanently so the demo can be set up again right away.
# Usage: ./scripts/teardown-entra.sh [--purge]
set -euo pipefail

purge=false
[[ "${1:-}" == "--purge" ]] && purge=true

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
graph="https://graph.microsoft.com/v1.0"
domain="$(az rest --method GET --url "$graph/organization?\$select=verifiedDomains" \
  --query "value[0].verifiedDomains[?isDefault].name | [0]" -o tsv)"
app_id="$(az ad app list --display-name cost-to-serve-demo --query "[0].appId" -o tsv)"
app_object_id="$(az ad app list --display-name cost-to-serve-demo --query "[0].id" -o tsv)"

echo "Will delete: app registration cost-to-serve-demo (${app_id:-not found}),"
echo "users dana.demo@$domain and luis.demo@$domain (if present)."
read -r -p "Type 'delete' to continue: " answer
[[ "$answer" == "delete" ]] || { echo "Cancelled. Nothing changed."; exit 1; }

deleted=()
[[ -z "$app_id" ]] || { az ad app delete --id "$app_id"; deleted+=("$app_object_id"); }
for alias in dana luis; do
  oid="$(az ad user list --filter "userPrincipalName eq '$alias.demo@$domain'" --query "[0].id" -o tsv)"
  [[ -z "$oid" ]] || { az ad user delete --id "$oid"; deleted+=("$oid"); }
done

if [[ "$purge" == true ]]; then
  for id in ${deleted[@]+"${deleted[@]}"}; do
    az rest --method DELETE --url "$graph/directory/deletedItems/$id" -o none && echo "Purged $id from the recycle bin."
  done
fi

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
if [[ "$purge" == true ]]; then
  echo "Done. Deleted permanently."
else
  echo "Done. Deleted objects stay in the directory's recycle bin for 30 days; add --purge to remove them now."
fi
