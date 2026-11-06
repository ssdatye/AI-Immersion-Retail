#!/usr/bin/env bash
# Provisions the hosted demo platform (Increment 5) with infra/main.bicep, into the dedicated
# resource group rg-cost-to-serve-demo only: APIM (front gate + AI gateway), App Service B2,
# Azure SQL serverless, Cosmos DB serverless, Key Vault, Log Analytics + App Insights, a
# GitHub deploy identity, a monthly budget alert and the Foundry account and model. Also writes
# the Foundry endpoint into the git-ignored local settings for local chat and evaluations.
#
# Default: what-if preview only (no changes, no cost).
# --apply: deploys after you type 'deploy'. BILLABLE. APIM Developer takes 30-60 minutes.
#
# Usage: ./scripts/deploy-azure.sh [--apply] [--yes] [--location eastus2]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib-azure.sh"

mode="what-if"
confirmed=false
location="eastus2"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --apply) mode="apply"; shift ;;
    --yes|-y) confirmed=true; shift ;;
    --location) location="$2"; shift 2 ;;
    *) echo "Unknown argument '$1'." >&2; exit 2 ;;
  esac
done

subscription="$(az account show --query "{name:name, id:id}" -o tsv)"
echo "Subscription: $subscription"
echo "Resource group: $demo_rg ($location). No other resource group is read or changed."

developer_id="$(az ad signed-in-user show --query id -o tsv)"
developer_login="$(az ad signed-in-user show --query userPrincipalName -o tsv)"
contact_email="$(az account show --query user.name -o tsv)"
# A new monthly budget must start in the current month; an existing one keeps its start date.
budget_start="$(az consumption budget show --budget-name cts-demo-monthly -g "$demo_rg" --query timePeriod.startDate -o tsv 2>/dev/null | cut -c1-10 || true)"
budget_start="${budget_start:-$(date -u +%Y-%m-01)}"

# Entra IDs and Luis's region come from the git-ignored local settings written by setup-entra.sh.
params="$(mktemp)"
trap 'rm -f "$params" "$outputs_file.tmp"' EXIT
python3 - "$repo_root/src/CostToServe.Api/appsettings.Local.json" "$params" "$location" "$developer_id" "$developer_login" "$contact_email" "$budget_start" <<'PY'
import json, os, sys
settings_path, out, location, dev_id, dev_login, email, budget_start = sys.argv[1:]
entra = {}
if os.path.exists(settings_path):
    entra = json.load(open(settings_path)).get("Entra", {})
values = {
    "location": location,
    "deployPlatform": True,
    "developerPrincipalId": dev_id,
    "developerLogin": dev_login,
    "contactEmail": email,
    "budgetStartDate": budget_start,
    "entraClientId": entra.get("ClientId", ""),
    "regionalOpsRegions": entra.get("RegionalOpsRegions", {}),
}
if not values["entraClientId"]:
    print("Note: no Entra app in local settings; the hosted studio will reject sign-in. Run ./scripts/setup-entra.sh first.", file=sys.stderr)
json.dump({"$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
           "contentVersion": "1.0.0.0",
           "parameters": {k: {"value": v} for k, v in values.items()}}, open(out, "w"))
PY

common=(--name cts-demo-platform --location "$location" --template-file "$repo_root/infra/main.bicep" --parameters "@$params")

if [[ "$mode" == "what-if" ]]; then
  az deployment sub what-if "${common[@]}" --exclude-change-types Ignore NoChange
  echo
  echo "Preview only; nothing changed. To deploy (billable): ./scripts/deploy-azure.sh --apply"
  exit 0
fi

if [[ "$confirmed" != true ]]; then
  echo "This creates billable resources (about \$79-110/month while they exist; see docs/adr/004)."
  read -r -p "Type 'deploy' to continue: " answer
  [[ "$answer" == "deploy" ]] || { echo "Cancelled. Nothing changed."; exit 1; }
fi

# Write to a temporary file first so a failed deployment keeps the previous outputs.
az deployment sub create "${common[@]}" --query properties.outputs -o json > "$outputs_file.tmp"
mv "$outputs_file.tmp" "$outputs_file"
echo "Wrote deployment outputs to $outputs_file (git-ignored)."
require_demo_rg
write_foundry_settings "$(azure_output foundryOpenAIEndpoint)" "$(azure_output modelDeploymentName)"
echo "Studio URL (after app deploy): $(azure_output studioUrl)"
echo "Next: ./scripts/seed-azure-sql.sh, ./scripts/deploy-app.sh, then"
echo "      ./scripts/setup-entra.sh --redirect-uri $(azure_output studioUrl | sed 's:/*$::')"
