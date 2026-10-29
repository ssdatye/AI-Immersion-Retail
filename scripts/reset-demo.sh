#!/usr/bin/env bash
# Resets the Cost-to-Serve demo to its seed baseline.
#   local (default): data/local decision log and chat transcripts.
#   azure: deletes every decision and chat transcript in the demo's Cosmos DB, then restarts the
#          web app so the Trust Center's live view (kept in memory) starts fresh, and waits until
#          the studio is healthy (about a minute). Seed facts in Azure SQL and the evaluation
#          summary are unchanged. Add --no-restart to skip the restart.
#   local: restart a running local API yourself to clear its Trust Center live view.
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
restart=true
args=()
for arg in "$@"; do
  if [[ "$arg" == "--no-restart" ]]; then restart=false; else args+=("$arg"); fi
done

env ASPNETCORE_ENVIRONMENT=Development dotnet run --project "$repo_root/src/CostToServe.Api" --no-launch-profile -- reset "${args[@]}"
[[ "$restart" == true ]] || exit 0

web_app="$(azure_output webAppName)"
studio_url="$(azure_output studioUrl)"
echo "Restarting $web_app so the Trust Center's live view starts fresh..."
az webapp restart -g "$demo_rg" -n "$web_app" -o none
sleep 20
for _ in $(seq 1 30); do
  if curl -fsS -m 20 "${studio_url}api/health" >/dev/null 2>&1; then
    echo "Studio is healthy: $studio_url"
    exit 0
  fi
  sleep 10
done
echo "The studio did not become healthy in 5 minutes. Check: az webapp log tail -g $demo_rg -n $web_app" >&2
exit 1
