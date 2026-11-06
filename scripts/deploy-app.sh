#!/usr/bin/env bash
# Builds the studio (front end + API) and deploys it to the demo web app with your Entra
# sign-in, then waits until it answers through the APIM front gate.
# GitHub Actions does the same through OIDC: .github/workflows/deploy-demo.yml.
# Usage: ./scripts/deploy-app.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib-azure.sh"
require_demo_rg

web_app="$(azure_output webAppName)"
studio_url="$(azure_output studioUrl)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

web="$repo_root/src/CostToServe.Web"
[[ -d "$web/node_modules" ]] || npm ci --prefix "$web" --no-audit --no-fund
npm run build --prefix "$web"
dotnet publish "$repo_root/src/CostToServe.Api" -c Release -o "$work/publish" --nologo
(cd "$work/publish" && zip -qr "$work/app.zip" .)

echo "Deploying to $web_app in $demo_rg..."
az webapp deploy -g "$demo_rg" -n "$web_app" --src-path "$work/app.zip" --type zip --restart true -o none

echo "Waiting for ${studio_url}api/health through APIM..."
for _ in $(seq 1 40); do
  if health="$(curl -fsS "${studio_url}api/health" 2>/dev/null)"; then
    echo "$health"
    echo "Studio: $studio_url"
    exit 0
  fi
  sleep 15
done
echo "The studio did not become healthy in 10 minutes. Check: az webapp log tail -g $demo_rg -n $web_app" >&2
exit 1
