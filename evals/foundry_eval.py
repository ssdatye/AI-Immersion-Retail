#!/usr/bin/env python3
"""Submit an evaluation run to the isolated Foundry project (proj-evals) and save the results.

Reads results.jsonl written by the .NET runner (evals/CostToServe.Evals), creates a Foundry
evaluation with built-in agent evaluators judged by the demo's model deployment, plus string
checks that carry the runner's deterministic results into Foundry, then polls until the run
finishes and writes foundry-run.json and foundry-output-items.json next to results.jsonl.

Standard library only; HTTP goes through curl (works behind TLS-inspecting proxies) with an
Entra token from the Azure CLI. Adapted from the MafTracingPoc reference (evals/6_run_foundry_eval.py).
Usage: python3 evals/foundry_eval.py <run folder> [--timeout-seconds 1800]
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import urllib.parse
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
OUTPUTS = REPO / "infra" / "azure-outputs.local.json"

# Built-in judges (LLM-graded) and the runner's deterministic checks.
JUDGES = ("task_adherence", "tool_call_accuracy", "intent_resolution", "groundedness")
DETERMINISTIC = ("tool_selection", "numeric_accuracy", "grounded_numbers", "decision")


def az_token() -> str:
    return subprocess.run(
        ["az", "account", "get-access-token", "--resource", "https://ai.azure.com", "--query", "accessToken", "-o", "tsv"],
        capture_output=True, text=True, check=True,
    ).stdout.strip()


def request(endpoint: str, token: str, path: str, body: dict | None = None) -> dict:
    command = ["curl", "-sS", "--max-time", "120", endpoint + path,
               "-H", "Authorization: Bearer " + token, "-H", "Content-Type: application/json",
               "-w", "\n%{http_code}"]
    if body is not None:
        command += ["-X", "POST", "--data-binary", "@-"]
    result = subprocess.run(command, input=json.dumps(body) if body is not None else None, capture_output=True, text=True)
    raw, _, status = result.stdout.rpartition("\n")
    if result.returncode or not status.startswith("2"):
        detail = ""
        try:
            detail = json.loads(raw).get("error", {}).get("message", "")[:300]
        except (ValueError, AttributeError):
            pass
        raise RuntimeError(f"Foundry {path.split('?')[0]}: curl {result.returncode}, HTTP {status} {detail}")
    return json.loads(raw)


def definition(name: str, model: str) -> dict:
    judges = [{
        "type": "azure_ai_evaluator",
        "name": judge,
        "evaluator_name": "builtin." + judge,
        "initialization_parameters": {"deployment_name": model},
        "data_mapping": {
            "query": "{{item.query}}",
            "response": "{{item.response}}",
            "tool_definitions": "{{item.tool_definitions}}",
        },
    } for judge in JUDGES]
    # Deterministic results from the runner: "pass" and "n/a" pass, "fail" fails.
    checks = [{
        "type": "string_check",
        "name": "check_" + check,
        "input": "{{item.check_" + check + "}}",
        "operation": "ne",
        "reference": "fail",
    } for check in DETERMINISTIC]
    properties = {
        "case_id": {"type": "string"}, "persona": {"type": "string"},
        "query": {"type": "array"}, "response": {"type": "array"}, "tool_definitions": {"type": "array"},
        **{"check_" + c: {"type": "string"} for c in DETERMINISTIC},
    }
    return {
        "name": name,
        "data_source_config": {"type": "custom", "item_schema": {
            "type": "object", "properties": properties,
            "required": ["case_id", "query", "response", "tool_definitions"],
        }},
        "testing_criteria": judges + checks,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("run_folder")
    parser.add_argument("--timeout-seconds", type=int, default=1800)
    args = parser.parse_args()
    folder = Path(args.run_folder).resolve()
    rows = [json.loads(line) for line in (folder / "results.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
    run_info = json.loads((folder / "run.json").read_text(encoding="utf-8"))
    outputs = json.loads(OUTPUTS.read_text(encoding="utf-8"))
    endpoint = outputs["evalsProjectEndpoint"]["value"]
    model = outputs["modelDeploymentName"]["value"]
    if not endpoint:
        sys.exit("No evalsProjectEndpoint in deployment outputs. Run ./scripts/deploy-azure.sh --apply.")

    token = az_token()
    connections = request(endpoint, token, "/connections?api-version=v1")
    # The evaluation project must stay isolated: no App Insights or storage connections.
    if not isinstance(connections.get("value"), list) or connections["value"]:
        sys.exit("proj-evals has connections; refusing to submit. Evaluations must run in a connection-free project.")

    name = f"cost-to-serve-{run_info['run_id']}"
    evaluation = request(endpoint, token, "/openai/v1/evals", definition(name, model))
    items = [{"item": {
        "case_id": r["id"], "persona": r["persona"],
        "query": r["foundry"]["query"], "response": r["foundry"]["response"],
        "tool_definitions": r["foundry"]["tool_definitions"],
        **{"check_" + c: r["checks"][c]["result"] for c in DETERMINISTIC},
    }} for r in rows]
    run = request(endpoint, token, f"/openai/v1/evals/{evaluation['id']}/runs", {
        "name": name,
        "data_source": {"type": "jsonl", "source": {"type": "file_content", "content": items}},
    })
    state = {"evalId": evaluation["id"], "runId": run["id"], "name": name, "model": model}
    print(f"Submitted Foundry evaluation {name} ({len(items)} rows). Polling...", flush=True)

    path = f"/openai/v1/evals/{evaluation['id']}/runs/{run['id']}"
    deadline = time.monotonic() + args.timeout_seconds
    while True:
        run = request(endpoint, token, path)
        if run["status"] in ("completed", "failed", "canceled", "cancelled"):
            break
        if time.monotonic() >= deadline:
            sys.exit(f"Run still {run['status']} after {args.timeout_seconds}s: {state}")
        time.sleep(15)
        token = az_token() if time.monotonic() % 600 < 15 else token

    output, after = [], None
    while True:
        page = request(endpoint, token, path + "/output_items?limit=100" + ("&after=" + urllib.parse.quote(after, safe="") if after else ""))
        output.extend(page["data"])
        if not page.get("has_more"):
            break
        after = page.get("last_id")

    (folder / "foundry-output-items.json").write_text(json.dumps({"data": output}, indent=2), encoding="utf-8")
    summary = {**state, "status": run["status"], "resultCounts": run.get("result_counts"),
               "perTestingCriteria": run.get("per_testing_criteria_results"), "outputItems": len(output)}
    (folder / "foundry-run.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({k: summary[k] for k in ("status", "resultCounts", "outputItems")}, indent=2))
    if run.get("report_url"):
        # Printed only: the portal URL contains subscription and resource IDs, so it is not written to the repo.
        print("Foundry portal:", run["report_url"])
    return 0 if run["status"] == "completed" and len(output) == len(items) else 1


if __name__ == "__main__":
    raise SystemExit(main())
