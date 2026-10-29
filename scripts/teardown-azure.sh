#!/usr/bin/env bash
# Shuts the demo down by deleting the dedicated resource group rg-cost-to-serve-demo and
# everything in it. This is the only way to stop all charges (APIM and the App Service plan
# bill while they exist). DESTRUCTIVE and irreversible. Touches no other resource group.
# Entra objects (app registration, demo users) are separate: ./scripts/teardown-entra.sh.
#
# --purge also purges the soft-deleted Key Vault, APIM and Foundry account so their names can
# be reused by a redeploy.
# Usage: ./scripts/teardown-azure.sh [--purge]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib-azure.sh"

purge=false
[[ "${1:-}" == "--purge" ]] && purge=true

if ! az group show -n "$demo_rg" -o none 2>/dev/null; then
  echo "Resource group $demo_rg does not exist. Nothing to delete."
  exit 0
fi

location="$(az group show -n "$demo_rg" --query location -o tsv)"
echo "Subscription: $(az account show --query name -o tsv)"
echo "Resources in $demo_rg that will be deleted:"
az resource list -g "$demo_rg" --query "[].{name:name, type:type}" -o table
read -r -p "Type the resource group name ($demo_rg) to delete it: " answer
[[ "$answer" == "$demo_rg" ]] || { echo "Cancelled. Nothing changed."; exit 1; }

kv="$(az resource list -g "$demo_rg" --resource-type Microsoft.KeyVault/vaults --query "[0].name" -o tsv)"
apim="$(az resource list -g "$demo_rg" --resource-type Microsoft.ApiManagement/service --query "[0].name" -o tsv)"
foundry="$(az resource list -g "$demo_rg" --resource-type Microsoft.CognitiveServices/accounts --query "[0].name" -o tsv)"

az group delete -n "$demo_rg" --yes
echo "Deleted $demo_rg."

if [[ "$purge" == true ]]; then
  [[ -n "$kv" ]] && az keyvault purge --name "$kv" --location "$location" && echo "Purged Key Vault $kv."
  [[ -n "$apim" ]] && az apim deletedservice purge --service-name "$apim" --location "$location" && echo "Purged APIM $apim."
  [[ -n "$foundry" ]] && az cognitiveservices account purge --name "$foundry" --resource-group "$demo_rg" --location "$location" && echo "Purged Foundry $foundry."
fi
rm -f "$outputs_file"
echo "Done. Charges for these resources have stopped."
