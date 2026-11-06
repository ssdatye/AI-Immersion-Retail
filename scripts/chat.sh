#!/usr/bin/env bash
# Terminal chat with the Cost-to-Serve agent (needs 'az login' and ./scripts/deploy-foundry.sh).
# Usage: ./scripts/chat.sh [--as dana|luis] [--ask "question"]... [--no-trace]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec dotnet run --project "$repo_root/src/CostToServe.Api" --no-launch-profile -- chat "$@"
