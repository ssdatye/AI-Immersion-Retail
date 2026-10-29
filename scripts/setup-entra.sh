#!/usr/bin/env bash
# Creates the demo's Entra ID objects in the signed-in tenant, then writes git-ignored
# local settings for the API. Creates only these objects and changes nothing else:
#   * App registration "cost-to-serve-demo": SPA redirect URIs, API scope access_as_user,
#     app roles FinanceAnalyst and RegionalOps, user assignment required
#   * Users dana.demo@<domain> and luis.demo@<domain> with generated passwords that must be
#     changed at first sign-in; passwords go only to secrets/demo-users.local.txt (git-ignored)
#   * App role assignments: Dana -> FinanceAnalyst, Luis -> RegionalOps (region Southeast)
# Re-running is safe: existing objects are reused and existing passwords are never reset.
# Usage: ./scripts/setup-entra.sh [--redirect-uri https://host] [--yes]
set -euo pipefail
umask 077

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app_name="cost-to-serve-demo"
redirects=("http://localhost:5080" "http://localhost:5173")
confirmed=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --redirect-uri) redirects+=("$2"); shift 2 ;;
    --yes) confirmed=true; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

graph="https://graph.microsoft.com/v1.0"
tenant_id="$(az account show --query tenantId -o tsv)"
domain="$(az rest --method GET --url "$graph/organization?\$select=verifiedDomains" \
  --query "value[0].verifiedDomains[?isDefault].name | [0]" -o tsv)"

echo "Tenant default domain: $domain"
echo "Will create or reuse: app registration '$app_name', users dana.demo@$domain and luis.demo@$domain,"
echo "and their app role assignments. SPA redirect URIs: ${redirects[*]}"
if [[ "$confirmed" != true ]]; then
  read -r -p "Type 'create' to continue: " answer
  [[ "$answer" == "create" ]] || { echo "Cancelled. Nothing changed."; exit 1; }
fi

# App registration with app roles.
app_id="$(az ad app list --display-name "$app_name" --query "[0].appId" -o tsv)"
if [[ -z "$app_id" ]]; then
  roles_file="$(mktemp)"
  cat > "$roles_file" <<JSON
[
  {"allowedMemberTypes": ["User"], "id": "$(uuidgen | tr 'A-Z' 'a-z')", "isEnabled": true, "value": "FinanceAnalyst",
   "displayName": "FinanceAnalyst", "description": "Network-wide cost view; approves or rejects actions."},
  {"allowedMemberTypes": ["User"], "id": "$(uuidgen | tr 'A-Z' 'a-z')", "isEnabled": true, "value": "RegionalOps",
   "displayName": "RegionalOps", "description": "Own region only; proposes and executes approved actions."}
]
JSON
  app_id="$(az ad app create --display-name "$app_name" --sign-in-audience AzureADMyOrg \
    --app-roles "@$roles_file" --query appId -o tsv)"
  rm -f "$roles_file"
  echo "Created app registration $app_name."
else
  echo "Reusing app registration $app_name."
fi
app_object_id="$(az ad app show --id "$app_id" --query id -o tsv)"

# API scope, v2 tokens, SPA redirect URIs and the permissions MSAL requests.
scope_id="$(az ad app show --id "$app_id" --query "api.oauth2PermissionScopes[?value=='access_as_user'].id | [0]" -o tsv)"
[[ -n "$scope_id" ]] || scope_id="$(uuidgen | tr 'A-Z' 'a-z')"
existing_redirects="$(az ad app show --id "$app_id" --query "spa.redirectUris" -o tsv)"
redirects_json="$(printf '%s\n' "${redirects[@]}" $existing_redirects | sort -u | python3 -c 'import json,sys;print(json.dumps([l.strip() for l in sys.stdin if l.strip()]))')"
body="$(mktemp)"
cat > "$body" <<JSON
{
  "identifierUris": ["api://$app_id"],
  "spa": {"redirectUris": $redirects_json},
  "api": {
    "requestedAccessTokenVersion": 2,
    "oauth2PermissionScopes": [{
      "id": "$scope_id", "value": "access_as_user", "type": "User", "isEnabled": true,
      "adminConsentDisplayName": "Use the Cost-to-Serve studio",
      "adminConsentDescription": "Lets the What-If Studio call the Cost-to-Serve API as the signed-in user.",
      "userConsentDisplayName": "Use the Cost-to-Serve studio",
      "userConsentDescription": "Lets the What-If Studio call the Cost-to-Serve API as you."
    }]
  },
  "requiredResourceAccess": [
    {"resourceAppId": "$app_id", "resourceAccess": [{"id": "$scope_id", "type": "Scope"}]},
    {"resourceAppId": "00000003-0000-0000-c000-000000000000", "resourceAccess": [
      {"id": "37f7f235-527c-4136-accd-4a02d197296e", "type": "Scope"},
      {"id": "14dad69e-099b-42c9-810b-d002981feec1", "type": "Scope"},
      {"id": "7427e0e9-2fba-42fe-b0c0-848c9e6a8182", "type": "Scope"}
    ]}
  ]
}
JSON
az rest --method PATCH --url "$graph/applications/$app_object_id" --headers "Content-Type=application/json" --body "@$body" >/dev/null
rm -f "$body"

# Service principal: only assigned users may sign in.
sp_id="$(az ad sp list --filter "appId eq '$app_id'" --query "[0].id" -o tsv)"
[[ -n "$sp_id" ]] || sp_id="$(az ad sp create --id "$app_id" --query id -o tsv)"
az ad sp update --id "$sp_id" --set appRoleAssignmentRequired=true >/dev/null
# Tenant-wide consent for the studio's own scope and sign-in scopes, so demo users get no prompt.
sleep 10
az ad app permission admin-consent --id "$app_id" >/dev/null

# Demo users with generated passwords (written only to the git-ignored secrets file).
secrets_file="$repo_root/secrets/demo-users.local.txt"
mkdir -p "$repo_root/secrets"
ensure_user() {
  local alias="$1" display="$2" title="$3" upn="$1.demo@$domain" oid password
  oid="$(az ad user list --filter "userPrincipalName eq '$upn'" --query "[0].id" -o tsv)"
  if [[ -z "$oid" ]]; then
    password="$(openssl rand -base64 24 | tr -d '/+=' | cut -c1-18)Aa1!"
    oid="$(az ad user create --display-name "$display" --user-principal-name "$upn" --password "$password" \
      --force-change-password-next-sign-in true --query id -o tsv)"
    printf '%s\t%s\t(temporary; must be changed at first sign-in)\n' "$upn" "$password" >> "$secrets_file"
    echo "Created user $upn." >&2
  else
    echo "Reusing user $upn (password unchanged)." >&2
  fi
  az rest --method PATCH --url "$graph/users/$oid" --headers "Content-Type=application/json" \
    --body "{\"jobTitle\": \"$title\"}" >/dev/null
  echo "$oid"
}
dana_oid="$(ensure_user dana "Dana (demo)" "Director, Fulfillment Finance (illustrative)")"
luis_oid="$(ensure_user luis "Luis (demo)" "Southeast Regional Delivery Ops Manager (illustrative)")"

assign_role() {
  local principal="$1" role_value="$2" role_id
  role_id="$(az ad app show --id "$app_id" --query "appRoles[?value=='$role_value'].id | [0]" -o tsv)"
  if [[ -z "$(az rest --method GET --url "$graph/servicePrincipals/$sp_id/appRoleAssignedTo" \
      --query "value[?principalId=='$principal' && appRoleId=='$role_id'].id | [0]" -o tsv)" ]]; then
    az rest --method POST --url "$graph/servicePrincipals/$sp_id/appRoleAssignedTo" --headers "Content-Type=application/json" \
      --body "{\"principalId\": \"$principal\", \"resourceId\": \"$sp_id\", \"appRoleId\": \"$role_id\"}" >/dev/null
  fi
}
assign_role "$dana_oid" FinanceAnalyst
assign_role "$luis_oid" RegionalOps

# Git-ignored local settings: the API switches from dev personas to Entra sign-in.
settings="$repo_root/src/CostToServe.Api/appsettings.Local.json"
python3 - "$settings" "$tenant_id" "$app_id" "$luis_oid" <<'PY'
import json, os, sys
path, tenant, client, luis = sys.argv[1:]
data = json.load(open(path)) if os.path.exists(path) else {}
data["Entra"] = {"TenantId": tenant, "ClientId": client, "RegionalOpsRegions": {luis: "Southeast"}}
with open(path, "w") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
PY

echo
echo "Done. Sign-in is configured in $settings (git-ignored)."
echo "Temporary passwords: $secrets_file (git-ignored). Each user must change it at first sign-in."
echo "Recommended: require MFA for these users (Security defaults, or Conditional Access with Entra ID P1)."
