---
title: Agent evaluations
description: How the Cost-to-Serve agent is evaluated, how to run the evaluation, how to read the report, and how to add cases.
ms.date: 2026-10-30
---

## Overview

The evaluation asks the agent 38 demo questions as Dana and as Luis and scores every answer two ways:

* **Deterministic checks**, computed by the runner without a model: did the agent call the right tools with the right arguments, quote the engine's numbers, quote only numbers that come from tools, and log decisions only when allowed?
* **Foundry judges**, LLM-graded in the isolated Foundry project `proj-evals`: task adherence, tool-call accuracy, intent resolution and groundedness.

The latest results are in [report.md](./report.md). Design decisions are in [ADR 005](../adr/005-agent-evaluations.md).

## Run it

```bash
az login
./scripts/run-evals.sh                  # runner + Foundry judges + report (about 5 minutes)
./scripts/run-evals.sh --skip-foundry   # deterministic checks only
./scripts/run-evals.sh --case dana-a1-a2-offset --skip-foundry   # one case
```

The script writes the run folder to `evals/out/<UTC timestamp>/` (git-ignored: it holds full answers and tool results) and regenerates `docs/evals/report.md` and `docs/evals/summary.json`. The summary (totals, every question with its answer, checks, judges and number sources, and the Foundry evaluation and run IDs) is what the studio's Trust Center shows as its acceptance tests; redeploy the app (`./scripts/deploy-app.sh`) to publish a new run. The Foundry step prints a portal link; the link is not saved because it contains subscription and resource IDs.

Prerequisites: the demo's Foundry account and model (`./scripts/deploy-foundry.sh` or `./scripts/deploy-azure.sh --apply`), which also create `proj-evals` and give you the Foundry User and Cognitive Services User roles on the account; .NET 10; Python 3 (standard library only); `curl`; the Azure CLI.

Cost: model tokens only. One run uses about 130,000 agent tokens plus the judges, roughly $0.10 to $0.50.

## What the runner does

| Step | Detail |
|---|---|
| Setup | Each case starts from its own in-memory decision log and package store. `setup` steps (for example "Luis proposes A2 at 30%", or Luis creating package P-SE-01) run first through the same tools. The demo's real state is never touched. |
| Expected numbers | The runner calls the tools named in `expected_numbers` directly, before the question, and reads the listed fields. Expectations therefore follow the engine and the seed data; nothing is typed by hand. |
| Agent turn | The question goes to the studio's own agent code: same instructions (`CostToServeAgentFactory`), same eight tools with the persona's scope, same model deployment. Tool calls are recorded with their arguments and results. Confirmation cards are answered with the case's `confirm` value. If the model deployment's content filter blocks the question (for example a jailbreak attempt), the case records `blocked_by: content_safety` and the same answer the studio shows. |
| Checks | Tool selection, numeric accuracy, numbers from tools, decision rules (see below). |
| Output | `results.jsonl` (one row per case, including the Foundry message format) and `run.json` (model, seed version, dataset hash, tokens). |

| Check | Passes when |
|---|---|
| Tool selection | Every `expected_tools` entry is matched by a call with those arguments (a subset; arrays mean any of, `null` means may be omitted), and no `forbid_tools` tool succeeded. |
| Numeric accuracy | Every expected number appears in the answer at the precision written (one unit of the last digit: 28,186,400 matches "$28.2M"), and no `absent_numbers` value appears. |
| Numbers from tools | Every number in the answer is in a tool result the agent received, the question or the instructions (small counts up to 13 are ignored). Anything else is a number the model made up or calculated. |
| Decision rules | With `expected_decision`, exactly that decision (action, decision, region, setting) is logged, or nothing when `recorded` is false. Without it, any logged decision fails the case. |

## Foundry judges

`evals/foundry_eval.py` submits each turn to `proj-evals` through the Foundry evaluations API (`/openai/v1/evals`), using only the Python standard library and `curl`, as in the MafTracingPoc reference. Each row carries the system prompt and question as `query`, the tool calls, tool results and answer as `response`, and the tool definitions. The judge is the demo's `gpt-4.1-mini` deployment.

| Evaluator | Measures |
|---|---|
| `builtin.task_adherence` | The agent followed its instructions and rules |
| `builtin.tool_call_accuracy` | The right tools with the right parameters, without redundancy |
| `builtin.intent_resolution` | The agent understood and addressed what the user asked |
| `builtin.groundedness` | The answer is supported by the tool results |
| `check_*` (string checks) | The runner's four deterministic results, so every score is visible in the portal |

The script refuses to submit if `proj-evals` has any connection, so evaluated content never mixes with telemetry.

**Reading tool-call accuracy:** when a tool refuses a call by design (Luis asking for the West, Luis approving, Dana executing, a guardrail breach, a declined confirmation), the judge scores the refused call down even though the agent behaved correctly. The report marks these with † as expected refusals.

## What the evaluation changed

The first runs found two real agent problems, fixed in the agent instructions:

| Finding | Fix |
|---|---|
| "Approve A1." with no region made the agent approve A1 for all three regions | Never log one decision for several regions unless the user names each; when no region is named, call `log_decision` without one and ask only if the tool asks |
| For a week-over-week change, the agent fetched two weeks and subtracted them itself ($0.47), against the numbers rule | Use `explain_variance`, which returns the change, instead of subtracting two results |

A first correction made the agent ask for a region even when only one region had a proposal; the evaluation caught that regression too, and the final wording passes all cases.

### Revision of October 30, 2026

The golden set grew from 27 to 35 cases to match the demo as it now runs: the studio's five question chips word for word, the exact message "Ask the agent about these changes" sends (change list plus studio settings), custom packages (Luis proposes one by chat, Dana approves one, Dana may not create one), what is waiting for Finance, a fuel forecast the agent has no data for, and a prompt-injection attempt. Three more cases cover revisions of an approved action (Luis proposes 30% instead of the approved 20%; Dana approves or declines it), added with that workflow, making 38. The new cases found these problems:

| Finding | Fix |
|---|---|
| "Execute A2." was answered "A2 is not yet approved" without calling a tool | Instructions: for propose, approve, reject or execute, always call `log_decision`; never state a status no tool returned |
| "Approve P-SE-01." made the agent ask for a region | Instructions: a package belongs to one region; call `log_decision` without a region |
| "What's waiting for my approval?" listed not-reviewed actions as waiting for Finance | `get_action_playbook` now returns `WaitingForYou` (Proposed items for finance, Approved for regional ops) with each item's regional saving; the agent reports that list |
| A net operating income impact of -$5.49M was described as "a cost reduction" | Instructions: a negative impact means the quarter still costs that much more; never call it a saving |
| The agent subtracted two weekly costs ("$630K a week") and rounded $906,358 to "about $900K" | `run_scenario` now returns `FuelShockWeeklyCostUsd` and `LeversWeeklySavingUsd`; instructions allow $906K but not a different figure |
| "How is this calculated?" asked cold got a clarifying question | Instructions: explain the cost-per-order method without asking back |
| The prompt-injection question was blocked by the deployment's content filter, which the studio showed as "model request failed" | The studio and the runner now show it as a refusal by policy: "blocked by Azure AI Content Safety; nothing was changed" |

## Add a case

Add one JSON object per line to [evals/dataset.jsonl](../../evals/dataset.jsonl):

```json
{"id": "dana-network-shock", "persona": "dana", "category": "scenario",
 "query": "What does a $0.50 per gallon diesel increase for 13 weeks cost the whole network this quarter?",
 "expected_tools": [{"name": "run_scenario", "args": {"diesel_change_usd_per_gallon": 0.5, "duration_weeks": 13}}],
 "expected_numbers": [{"tool": "run_scenario", "args": {"diesel_change_usd_per_gallon": 0.5, "duration_weeks": 13}, "fields": ["fuelShockQuarterCostUsd"]}]}
```

| Field | Meaning |
|---|---|
| `id`, `persona`, `category`, `query` | Required. `persona` is `dana` or `luis` |
| `setup` | Decisions logged before the question: `persona`, `action`, `decision`, optional `region`, `setting`, `reason`. With `decision: "package"`, the persona creates and proposes a package from `name` and `levers` (for example `{"batch": 0.35, "cap": true}`) |
| `confirm` | How the confirmation card is answered (default `false`) |
| `expected_tools` | Calls that must happen: `name` and `args`, or `any_of` a list of such calls |
| `forbid_tools` | Tools that must not succeed |
| `expected_numbers`, `absent_numbers` | `tool`, `args`, optional `as` persona, and `fields` paths such as `drivers[0].sharePct` |
| `expected_decision` | `recorded`, and when true `action`, `decision`, `region`, `setting`, and for a package proposal `levers` |
| `notes` | What a good answer does; shown to reviewers |

`dotnet test` validates the dataset (unique IDs, 20 to 40 cases, both personas).
