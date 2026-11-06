#!/usr/bin/env bash
# Shared helpers for the Azure scripts. Source it; do not run it.
# Reads infra/azure-outputs.local.json (git-ignored), written by ./scripts/deploy-azure.sh --apply.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
outputs_file="$repo_root/infra/azure-outputs.local.json"
demo_rg="rg-cost-to-serve-demo"

# Prints one deployment output value, or fails with a hint.
azure_output() {
  if [[ ! -f "$outputs_file" ]]; then
    echo "No deployment outputs at $outputs_file. Run ./scripts/deploy-azure.sh --apply first." >&2
    return 1
  fi
  python3 -c 'import json,sys; v=json.load(open(sys.argv[1]))[sys.argv[2]]["value"]; print(v) if v else sys.exit(f"Output {sys.argv[2]} is empty.")' "$outputs_file" "$1"
}

# Refuses to continue unless the deployment targets the dedicated demo resource group.
require_demo_rg() {
  local rg
  rg="$(azure_output resourceGroupName)"
  if [[ "$rg" != "$demo_rg" ]]; then
    echo "Refusing: outputs point at resource group '$rg', not '$demo_rg'." >&2
    exit 1
  fi
}

# Writes the Foundry endpoint and model deployment into the git-ignored local settings for local chat
# and evaluations, keeping every other section (for example Entra from setup-entra.sh).
write_foundry_settings() {
  python3 - "$repo_root/src/CostToServe.Api/appsettings.Local.json" "$1" "$2" <<'PY'
import json, os, sys
path, endpoint, deployment = sys.argv[1:]
settings = json.load(open(path)) if os.path.exists(path) else {}
settings["Foundry"] = {"Endpoint": endpoint, "Deployment": deployment}
with open(path, "w") as f:
    json.dump(settings, f, indent=2)
    f.write("\n")
print(f"Wrote Foundry settings to {path} (git-ignored)")
PY
}
