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
* **Privacy.** Each user sees only their own answers. Decision counts follow the caller's scope. Resource IDs for the portal links come from app settings at runtime, never from the repository.

## Consequences

* The session view resets when the app restarts and is per instance; Application Insights remains the system of record (option C stays open for production).
* The evaluation summary is as fresh as the last `./scripts/run-evals.sh` and deployment.
* The receipt adds a few milliseconds of server work per answer and no model calls.
