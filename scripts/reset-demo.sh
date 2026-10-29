#!/usr/bin/env bash
# Resets the Cost-to-Serve demo to its seed baseline.
#   local (default): data/local decision log and chat transcripts.
#   azure: deletes every decision and chat transcript in the demo's Cosmos DB, then restarts
#          the demo web app so it reloads. Seed facts in Azure SQL are unchanged.
# Usage: ./scripts/reset-demo.sh [--target local|azure] [--yes]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="local"
for ((i = 1; i <= $#; i++)); do
  if [[ "${!i}" == "--target" ]]; then
    next=$((i + 1))
    target="${!next:-local}"
  fi
done

if [[ "$target" != "azure" ]]; then
  exec dotnet run --project "$repo_root/src/CostToServe.Api" --no-launch-profile -- reset "$@"
fi

source "$repo_root/scripts/lib-azure.sh"
require_demo_rg
export CostToServe__Cosmos__Endpoint="$(azure_output cosmosEndpoint)"
export CostToServe__Cosmos__Database="$(azure_output cosmosDatabaseName)"
ASPNETCORE_ENVIRONMENT=Development dotnet run --project "$repo_root/src/CostToServe.Api" --no-launch-profile -- reset "$@"
web_app="$(azure_output webAppName)"
az webapp restart -g "$demo_rg" -n "$web_app" -o none
echo "Restarted $web_app."
