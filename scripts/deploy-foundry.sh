#!/usr/bin/env bash
# Deploys the demo's Foundry account and model into the dedicated resource group
# (infra/main.bicep), then writes git-ignored local settings for the agent.
# Billable: creates Azure resources. Touches no other resource group.
# Only for local agent chat without the hosted platform; ./scripts/deploy-azure.sh --apply
# deploys Foundry too and writes the same settings.
# Usage: ./scripts/deploy-foundry.sh [--location eastus2]
set -euo pipefail

# shellcheck source=lib-azure.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib-azure.sh"
location="eastus2"
if [[ "${1:-}" == "--location" && -n "${2:-}" ]]; then location="$2"; fi

developer_id="$(az ad signed-in-user show --query id -o tsv)"
outputs="$(az deployment sub create \
  --name cts-demo-foundry \
  --location "$location" \
  --template-file "$repo_root/infra/main.bicep" \
  --parameters location="$location" developerPrincipalId="$developer_id" \
  --query properties.outputs -o json)"

value() { OUTPUTS="$outputs" python3 -c 'import json,os,sys; print(json.loads(os.environ["OUTPUTS"])[sys.argv[1]]["value"])' "$1"; }
echo "Resource group: $(value resourceGroupName)"
echo "Foundry account: $(value foundryAccountName)"
write_foundry_settings "$(value foundryOpenAIEndpoint)" "$(value modelDeploymentName)"
