#!/usr/bin/env bash
# Runs the agent evaluation end to end and writes docs/evals/report.md.
#   1. evals/CostToServe.Evals asks every question in evals/dataset.jsonl as Dana or Luis through the
#      studio's agent code (same instructions, tools and model) and scores the deterministic checks.
#   2. evals/foundry_eval.py submits the turns to the isolated Foundry project proj-evals for the
#      built-in judges (skip with --skip-foundry).
#   3. evals/report.py writes the report.
# Billable: model tokens only, roughly $0.10-0.50 per run. Needs 'az login'.
# Usage: ./scripts/run-evals.sh [--skip-foundry] [--case <id> ...]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
skip_foundry=false
runner_args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-foundry) skip_foundry=true; shift ;;
    --case) runner_args+=(--case "$2"); shift 2 ;;
    *) echo "Unknown argument '$1'. Usage: ./scripts/run-evals.sh [--skip-foundry] [--case <id> ...]" >&2; exit 2 ;;
  esac
done

run_dir="$repo_root/evals/out/$(date -u +%Y%m%d-%H%M%S)"
dotnet run --project "$repo_root/evals/CostToServe.Evals" -- --out "$run_dir" ${runner_args[@]+"${runner_args[@]}"}

if [[ "$skip_foundry" != true ]]; then
  python3 "$repo_root/evals/foundry_eval.py" "$run_dir"
fi

python3 "$repo_root/evals/report.py" "$run_dir"
echo "Run folder (git-ignored): $run_dir"
