---
title: "ADR 006: Trust Center and live evidence"
description: "How the studio shows its evaluation results, live grounding evidence and traces to the people using it."
ms.date: 2026-10-29
status: Accepted
---

## Context

The demo's claims are that numbers come from a deterministic engine, that people make every decision, and that every step is traced. The evaluation (ADR 005) proves the first two offline, and Application Insights holds the traces, but neither was visible in the studio. The owner asked for these to be front and center for the customer demo.

## Options considered

| Option                                                    | Pros                                                                  | Cons                                                          | Effort | Risk   |
|-----------------------------------------------------------|-----------------------------------------------------------------------|---------------------------------------------------------------|--------|--------|
| A: Static Trust tab with the last evaluation summary      | Simple; no runtime dependencies                                       | Nothing live; reads as a slide                                | Low    | Low    |
| B: Trust Center tab plus a live evidence receipt (chosen) | Live proof on every answer; ties the evaluation to what the user sees | Server-side check on each answer; in-memory session view only | Medium | Low    |
| C: B, with KQL queries against Application Insights       | History across restarts and instances                                 | Ingestion delay of minutes; reader role and query cost        | High   | Medium |
| D: Portal links only                                      | No code                                                               | Leaves the studio; weak story                                 | Low    | Low    |

The owner chose B.

## Decision

* **Live evidence on every answer.** After an answer streams, the API runs the same grounding check as the evaluation (`NumberEvidence.Trace`, shared by the evaluation runner) over the answer and that turn's tool results. Each number is mapped to a tool field, the question or the agent instructions; small counts need no source. The browser receives an `evidence` server-sent event and shows a receipt: verified, numbers to review (amber, not red: the check proves grounding, not correctness), refused by policy, or logged after a person confirmed.
* **Trust Center tab** (`GET /api/trust`). It shows the last evaluation summary (`docs/evals/summary.json`, written by `evals/report.py` and shipped with the app), this session's live KPIs, the caller's own answers, the span timeline of the latest answer, links to Foundry and Application Insights, and the controls in force.
* **Spans in memory.** A small OpenTelemetry processor keeps the last 100 traces' request, agent, model, tool and Cosmos spans. The time `log_decision` waits for a person is shown as the person's, not the engine's. The same spans still go to Application Insights.
* **Frontier fit and acceptance tests** (revision, same day). The owner asked that the tab show why this is a Frontier pattern and meet the delivery rubric. A strip at the top maps each of the five Frontier tests to its proof in the demo, with a link to it, and shows the four mandatory gates; the executive sponsor gate is marked customer-owned rather than claimed. The evaluation is presented as acceptance tests: the summary now lists every question with its answer, checks, judges and number sources (recorded by the runner with `NumberEvidence.Trace`), plus the Foundry evaluation and run IDs, so the app links straight to the run. A self-scored rubric was rejected: the rubric scores the statement of work, and grading ourselves in the product would weaken credibility. An assumptions-and-limits table states what the demo does not prove and how each is validated.
* **Demo reset.** The acceptance tests ship with the app, so a reset keeps them. `reset-demo.sh --target azure` restarts the web app so the in-memory live view starts again with the reset.
* **Studio layout** (revision, front end only). The conversation is the main view; the quarter's outcomes are pinned above it; the model (fuel, levers, outcome details) and the decisions open in a side panel only when wanted. Model changes are listed as "not shared with the agent" until the person asks the agent about them, which sends the change list with the settings as context (the existing mechanism). The per-answer proof expands to the tool calls in order and the field each number came from. The Trust Center's trace lists the named steps plus "Other" so the steps add up to the total. No API or contract changed; applying an answer's tool inputs back to the model would need the tool arguments in the chat events, which is left for later.
* **Model tokens and cost** (revision, October 30). The owner asked to show tokens and their cost per call. Model-call spans already carry token counts (OpenTelemetry GenAI conventions, including cached input), so the span store keeps them; each answer's usage gives its total. The API prices them with `Trust:Pricing` (default: Azure list price for gpt-4.1-mini Global Standard, East US 2, from the Retail Prices API: $0.40 input, $0.10 cached input, $1.60 output per million tokens) and returns the cost per model call, per answer and per session, the evaluation run's cost (agent tokens, now in the summary), and a monthly projection at 1,000 answers a day from the evaluation average. Tools use no tokens, which the trace shows as $0. Options considered: cost in the existing trace and receipts (chosen, with the projection), a separate cost section with charts, or links to Application Insights or gateway token metrics. Labels state list price and what is excluded (gateway, hosting, evaluation judges).
* **Privacy.** Each user sees only their own answers; totals across users are counts only, never questions. Decision counts follow the caller's scope. Resource IDs for the portal links come from app settings at runtime, never from the repository.

## Consequences

* The session view resets when the app restarts and is per instance; Application Insights remains the system of record (option C stays open for production).
* The evaluation summary is as fresh as the last `./scripts/run-evals.sh` and deployment.
* The receipt adds a few milliseconds of server work per answer and no model calls.
