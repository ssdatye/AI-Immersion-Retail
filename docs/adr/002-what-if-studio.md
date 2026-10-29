---
title: "ADR 002: What-If Studio front end"
description: "Increment 3 decisions for the What-If Studio: front-end stack, hosting, chat streaming, confirmation cards and shared scope."
ms.date: 2026-10-28
status: Accepted
---

## Context

Increment 3 adds the What-If Studio: a fuel price panel, impact levers, scenario outcomes,
agent chat and approve or reject decisions, wired to the local API. Teams is out of scope.

## Decisions

### Stack and hosting

* Vite 8, React 19 and TypeScript 5.9 in `src/CostToServe.Web`. TypeScript 7 was available
  but is a new toolchain; 5.9 keeps risk low for a demo.
* Charts are small SVG components, with no chart library.
* `npm run build` writes to `src/CostToServe.Api/wwwroot` (git-ignored). The API serves the
  studio with an SPA fallback, so one App Service hosts both, as planned for Increment 5.
* `npm run dev` proxies `/api` to the API for hot reload.

### One scope path for UI and agent

* UI endpoints call the same `CostToServeTools` methods the agent uses, created per caller.
  The studio and the agent therefore show identical numbers and the same scope rules.
* Tool error objects map to HTTP status: `scope_denied` is 403, `invalid_request` is 400.

### Chat streaming and confirmation cards

* `POST /api/chat` returns server-sent events: `start`, `delta`, `tool`, `confirmation`,
  `confirmation_closed`, `error` and `done` with the trace ID. The browser reads it with
  `fetch` and a tested incremental parser, because `EventSource` cannot send a POST body
  or custom headers.
* SSE frames use single-line JSON. The shared AI serializer options write indented JSON,
  which broke framing until fixed.
* When the agent calls `log_decision`, the web confirmer emits a `confirmation` card and
  waits for `POST /api/confirmations/{id}`. Only the card owner can resolve it. Timeouts
  and disconnects count as declined. The terminal confirmer uses the same interface.
* Button decisions in the actions table go through the same tool and rules; the click is
  the human confirmation.
* Conversations are in memory, bounded to 200, and bound to their owner. A conversation ID
  is not authorization.
* The chat can append the current studio settings as reference context. The agent still
  uses tools for every number.

### Decision workflow

Each playbook action moves through one workflow per region, enforced on the server for
buttons and the agent alike:

```text
Not reviewed -> Proposed (RegionalOps) -> Approved or Rejected (FinanceAnalyst) -> Executed (RegionalOps)
```

* Finance may approve or reject without a proposal, and may reject (revoke) an approval
  before execution. A rejected action can be proposed or approved again.
* Executed is final until the demo is reset.
* Duplicate or out-of-order steps return a `conflict` tool result and HTTP 409.
* The playbook returns each action's `status` and the `availableDecisions` for the caller.
  The UI shows a status badge, offers only those buttons, and confirms in the row.

* Approved and executed actions are applied to the main Scenario outcomes automatically: the
  scenario call sends them as `actionIds` together with the what-if levers, and the server takes
  the larger value of each lever. Approving or revoking changes the headline numbers at once.
  Approved actions appear as locked chips under Impact levers, and the sliders and switches
  show the effective values: the approved level is a floor (green), and dragging above it adds a
  what-if on top. Only actions that set a fee affect the fee, so a what-if fee cut is kept when
  actions are combined (server and browser share this rule). An "Apply to outcomes" toggle
  removes the plan to compare against the fuel change and levers alone. (A first version showed
  the plan in a separate box while the headline numbers ignored it, which confused users.)

* Decisions can also be made in chat without the model: decision buttons under agent replies
  for the action IDs the reply mentions (status and buttons come from the live playbook, not
  the model's text), and slash commands (`/approve A2 reason`, `/status`, `/help`) parsed in
  the browser and sent straight to `POST /api/decisions`. The agent is told to name actions by
  ID so the buttons appear. The model-driven path (`log_decision` plus a confirmation card)
  remains.

* Execute has a visible payoff: an Execution results readout (and the `get_execution_results`
  tool) ported from the reference demo's "Next day: the result" step. Southeast zones 1-3 are
  the pilot and run the executed actions; zones 4-7 are the control, assumed flat apart from
  +0.2% drift. It shows Express cost per order before and after, and the weekly saving if
  scaled across the region. Zone-level inputs are aggregated from the seed like region inputs,
  and parity tests match the reference's pilot numbers. It is a modeled readout, not
  measured telemetry.

* Action lever settings are configuration, not code. `data/config/action-levers.csv` lists
  each action's levers and a `source` column. A1-A3 come from the reference demo's action
  list; A4-A6 are approximated from the playbook effect text. `ActionCatalog` loads the file
  at startup (`CostToServe:ActionConfigFile`) and falls back to the built-in defaults when
  the file is missing; invalid rows fail startup. The playbook and UI show the source.

* Settings are adjustable per decision. An action whose levers are a single share lever (A1
  batch, A2 closer store, A4 split reduction, A6 pickup) exposes that lever as its setting;
  A3 and A5 are on/off. Propose and approve take an optional `setting` (a fraction, range
  checked against the What-If limits). Without one, approve keeps the proposed value and
  propose uses the playbook default. Repeating the current step with a different value
  changes it: regional ops revises a pending proposal, and finance changes an approval
  before execution. Repeating it with the same value is a conflict. Reject resets to the
  playbook default. Execute records the approved value.
* Every propose, approve and execute row logs the resolved setting in a trailing `setting`
  column of `decision_log.csv`. The seed template keeps its schema; the local log gains the
  column on its first write, and older rows read as "no setting". Options considered were a
  log column (chosen: one source of truth, audit trail), a separate overrides file (two
  files to keep consistent), and a JSON blob in the reason column (overloads a human field).
* The decided setting drives everything: `run_scenario` and `check_guardrail` apply each
  listed action at its decided value, the playbook reports savings and guardrail status at
  that value, approve and execute re-check the guardrail at it (so A1 at 50% batching is
  blocked), and execution results use it. The UI's lever floors and chips use the
  playbook's decided `levers`.

* Work is surfaced to the role that must act next. The header badge counts actions waiting
  on the caller (Proposed for FinanceAnalyst, Approved for RegionalOps), those rows sort first
  and are highlighted, and the chat shows a waiting banner with decision buttons. Locked
  lever chips link to their row, where Revoke undoes an approval. There are no push
  notifications. Both users see the shared decision log when they load or refresh the studio.

* Decisions are per region. A first version had no region, so Dana approving Luis's
  Southeast proposal changed the whole network's numbers. Options considered: region-scoped
  decisions (chosen), keeping network-wide approvals with clearer labels, or a Southeast pilot
  followed by a "scale to network" step (which can still be added on top). Each log row now
  has a `region` (CSV column, Cosmos field); rows logged before that count as Southeast.
  Regional ops always decide for their own region; finance names the region, or the tool uses
  the one region where the action is proposed. Scenarios take `applyPlan` and apply each
  region's approved and executed actions in that region only. The engine sums per-region
  scenarios, which equals the network scenario when every region has the same levers (tested).
  Express within 3 hours is the lowest region's, because the guardrail must hold everywhere.
  The UI adds "Decide for" region tabs with waiting counts for finance, shows the region on
  the waiting badge, chat banner, plan and decision log, and accepts a region in commands.

User feedback after Increment 4: the first version of the buttons gave too little feedback.
A success line appeared at the top of the panel and the buttons never changed, so a click
seemed to do nothing. The workflow above replaced it.

### Caller identity until Entra

* `X-Demo-Persona: dana|luis` identifies the caller only when `CostToServe:DevPersonas` is
  true, which `appsettings.Development.json` sets. Otherwise the API returns 401. Increment 4
  replaces this with Entra tokens and app roles.

## Verification

* 12 in-memory API tests cover personas, scope, decisions, confirmation ownership and
  input errors; 5 front-end unit tests cover the SSE parser and formatting.
* A browser run showed Dana applying A1 and A2 (88.1% offset, 84.3% Express within 3
  hours), the agent's tool chips and confirmation card, and the logged approval. Luis saw
  Southeast only, was refused executing an unapproved action, and executed the approved one.

## Consequences

* Chat history lives in API memory and is lost on restart; durable threads come with
  Cosmos DB in Increment 5.
* The dev persona header must never be enabled outside local development.
