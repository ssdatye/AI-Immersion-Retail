#!/usr/bin/env bash
# Deploys the demo's Foundry account and model into the dedicated resource group
# (infra/main.bicep), then writes git-ignored local settings for the agent.
# Billable: creates Azure resources. Touches no other resource group.
# Usage: ./scripts/deploy-foundry.sh [--location eastus2]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
location="eastus2"
if [[ "${1:-}" == "--location" && -n "${2:-}" ]]; then location="$2"; fi

developer_id="$(az ad signed-in-user show --query id -o tsv)"
outputs="$(az deployment sub create \
  --name cts-demo-foundry \
  --location "$location" \
  --template-file "$repo_root/infra/main.bicep" \
  --parameters location="$location" developerPrincipalId="$developer_id" \
  --query properties.outputs -o json)"

settings="$repo_root/src/CostToServe.Api/appsettings.Local.json"
OUTPUTS="$outputs" python3 - "$settings" <<'PY'
import json, os, sys
o = json.loads(os.environ["OUTPUTS"])
settings = {"Foundry": {
    "Endpoint": o["foundryOpenAIEndpoint"]["value"],
    "Deployment": o["modelDeploymentName"]["value"],
}}
with open(sys.argv[1], "w") as f:
    json.dump(settings, f, indent=2)
    f.write("\n")
print(f"Resource group: {o['resourceGroupName']['value']}")
print(f"Foundry account: {o['foundryAccountName']['value']}")
print(f"Wrote {sys.argv[1]} (git-ignored)")
PY
