#!/usr/bin/env bash
# Builds the What-If Studio front end and runs the API that serves it on http://localhost:5080.
# Chat needs 'az login' and ./scripts/deploy-foundry.sh; the rest works offline in CSV mode.
# Usage: ./scripts/run-studio.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
web="$repo_root/src/CostToServe.Web"
if [[ ! -d "$web/node_modules" ]]; then
  npm ci --prefix "$web" --no-audit --no-fund
fi
npm run build --prefix "$web"
exec dotnet run --project "$repo_root/src/CostToServe.Api"
