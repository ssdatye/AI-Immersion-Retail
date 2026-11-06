---
title: "ADR 001: Agent, model access and tools"
description: "Increment 2 decisions for the Cost-to-Serve agent: Foundry model access, tool design, caller scope, decisions and tracing."
ms.date: 2026-10-28
status: Accepted
---

## Context

Increment 2 adds a Microsoft Agent Framework (MAF) agent whose numbers come only from
tools over the deterministic cost engine, with tracing. The user chose option A: create
the dedicated resource group early, with only a Foundry account and a GPT-4.1-mini
deployment, so the agent runs against a real model from the start.

## Decisions

### Model access

* Bicep in `infra/` creates `rg-cost-to-serve-demo` (East US 2), a Foundry AIServices
  account with key auth disabled, and a `gpt-4.1-mini` (2025-04-14) GlobalStandard
  deployment at 30K TPM with the default content filter (`Microsoft.DefaultV2`).
* The developer gets "Cognitive Services OpenAI User" on that account only, for local
  Entra-authenticated calls. No subscription-scope or other-group assignments.
* The client chain is copied from the MafTracingPoc reference (MIT): `OpenAIClient` with a
  placeholder key, an HTTP handler that supplies an Entra bearer token, and the Responses
  API with stored output disabled. Locally the agent calls the Foundry endpoint directly;
  Increment 5 points the same chain at the APIM AI gateway.
* MAF 1.22.0 with OpenAI 2.13.0 and System.ClientModel 1.15.0, pinned as in the reference.

### Tools

| Tool                  | Purpose                                                       |
|-----------------------|---------------------------------------------------------------|
| `get_cost_per_order`  | Cost per order from daily facts by region, zone, type, dates  |
| `explain_variance`    | Week-over-week change: fuel, route density, split shipments   |
| `run_scenario`        | Diesel shock with levers or playbook actions over 13 weeks    |
| `check_guardrail`     | Express at least 80% within 3 hours, plus other guardrails    |
| `get_action_playbook` | Approved actions with modeled weekly savings and decisions    |
| `log_decision`        | Propose, approve, reject or execute, after human confirmation |

* `get_action_playbook` was added beyond the original list. The agent may recommend only
  playbook actions, so it needs a tool-backed list with savings.
* `run_scenario` and `check_guardrail` accept `action_ids` (for example `A1,A2`). In live
  testing the model ran A1 and A2 separately, and once added their savings itself.
  Combining actions in the host makes the 88% offset a single deterministic call.
* A1-A3 lever settings come from the reference demo. A4-A6 are derived from the playbook
  text and are labeled as approximations in tool output.
* Tools return rounded figures with units in field names, plus scope and data notices.
  Errors return `scope_denied` or `invalid_request` objects instead of throwing.

### Caller scope and decisions

* The host fixes the caller when it creates the tools; no tool accepts identity, role or
  scope arguments. A test checks the tool schemas for this.
* Until Entra sign-in (Increment 4), local chat uses illustrative personas: Dana
  (FinanceAnalyst, network-wide) and Luis (RegionalOps, Southeast only). This is a local
  development stand-in, not authentication.
* Separation of duties: FinanceAnalyst approves or rejects (reject needs a reason);
  RegionalOps proposes and executes, and execute needs a prior approval. Approval and
  execution re-check the Express guardrail.
* `log_decision` asks a host confirmer before writing. The terminal asks the user to type
  `yes`; the web UI will use a confirmation card. Only `status: recorded` means logged.
* CSV mode writes to `data/local/decision_log.csv` using the seed schema; the `approver`
  column holds the acting person. Reset restores it.

### Tracing

* MAF `UseOpenTelemetry` emits `invoke_agent`, `chat` and `execute_tool` spans; HTTP
  client instrumentation adds the model calls. Tools add metadata-only tags: caller role,
  data scope, outcome and decision. Prompt and response capture is off by default.
* The terminal prints a span summary after each turn. Setting
  `OTEL_EXPORTER_OTLP_ENDPOINT` also exports via OTLP. Application Insights arrives with
  Increment 5.

## Consequences

* Live runs reproduced the anchors through the agent: 56.7% fuel share, $28.2M network
  shock and 88.1% offset for A1 plus A2 with 84.25% of Express within 3 hours.
* The model can still choose wording. Numeric accuracy will be measured with evaluations
  in Increment 6, not assumed.
* Token cost is pay-per-use, at about 3,500-6,500 tokens per turn in testing. Deleting the
  resource group removes all charges for this increment.
