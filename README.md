---
title: Fuel and Cost-to-Serve Intelligence What-If Studio
description: Customer demo of a deterministic cost-to-serve engine, agent and What-If Studio over illustrative retail delivery data.
---

## Overview

A customer demo: "Walmart Fuel and Cost-to-Serve Intelligence, What-If Studio". It uses
**illustrative data only**. No figures are real Walmart data.

Numbers come from a deterministic cost engine, never from a language model. The engine is a
C# port of the reference demo in
[docs/reference/walmart-v3-cost-to-serve-demo.html](docs/reference/walmart-v3-cost-to-serve-demo.html).
Decisions are in [docs/adr/000-reuse-assessment.md](docs/adr/000-reuse-assessment.md),
[docs/adr/001-agent-and-model-access.md](docs/adr/001-agent-and-model-access.md),
[docs/adr/002-what-if-studio.md](docs/adr/002-what-if-studio.md),
[docs/adr/003-entra-sign-in.md](docs/adr/003-entra-sign-in.md) and
[docs/adr/004-hosted-platform.md](docs/adr/004-hosted-platform.md).

The architecture as built, with technology choices, Dana and Luis sequence diagrams, cost and
the production view, is in
[docs/architecture/cost-to-serve-architecture.html](docs/architecture/cost-to-serve-architecture.html)
(version 2.0). Open it in a browser; the diagrams render with Mermaid from a CDN.

| Increment | Scope                                                    | Status  |
|-----------|----------------------------------------------------------|---------|
| 0         | Reference assessment                                     | Done    |
| 1         | Assets, scaffold, CSV mode, cost engine and tests, reset | Done    |
| 2         | Agent Framework agent with cost tools, tracing           | Done    |
| 3         | What-If Studio front end                                 | Done    |
| 4         | Entra sign-in, app roles, Dana and Luis views            | Done    |
| 5         | Azure resources via Bicep in a new resource group        | Done    |
| 6         | Evaluations                                              | Planned |
| 7         | Demo hardening and run-book                              | Planned |

## Repository layout

| Path                                | Contents                                                                                                                  |
|-------------------------------------|---------------------------------------------------------------------------------------------------------------------------|
| `src/CostToServe.Core`              | Cost engine, seed CSV loading, cost facts, local demo state                                                               |
| `src/CostToServe.Agent`             | Agent Framework agent, tools, caller scope, decision log                                                                  |
| `src/CostToServe.Api`               | ASP.NET Core host: studio API, chat stream, `chat`, `reset` and `seed-sql` commands, Azure data mode                      |
| `src/CostToServe.Web`               | What-If Studio front end (Vite, React, TypeScript), built into the API                                                    |
| `tests/CostToServe.Core.Tests`      | Parity, anchor, validation, data and reset tests                                                                          |
| `tests/CostToServe.Agent.Tests`     | Tool scope, decision rules and tool-schema tests (no model)                                                               |
| `tests/CostToServe.Api.Tests`       | In-memory API tests: personas, scope, decisions, confirmations                                                            |
| `infra`                             | Bicep: dedicated resource group, Foundry, and the hosted platform (APIM, App Service, SQL, Cosmos, Key Vault, monitoring) |
| `infra/apim`                        | APIM policies: studio front gate and AI gateway                                                                           |
| `data/seed`                         | Read-only illustrative seed CSVs with a `SHA256SUMS` manifest                                                             |
| `data/config/action-levers.csv`     | Lever settings for playbook actions A1-A6 (editable, with sources)                                                        |
| `data/local`                        | Mutable local demo state (git-ignored, recreated by reset)                                                                |
| `docs/reference`                    | Reference demo and architecture page, with hashes                                                                         |
| `docs/architecture`                 | Architecture as built (v2.0, supersedes the reference architecture page)                                                  |
| `tools/extract-html-fixture.mjs`    | Regenerates the golden parity fixture from the reference HTML                                                             |
| `scripts/reset-demo.sh`             | Resets the demo to its seed baseline                                                                                      |
| `scripts/deploy-foundry.sh`         | Deploys the Foundry model (billable) and writes local settings                                                            |
| `scripts/chat.sh`                   | Terminal chat with the agent                                                                                              |
| `scripts/run-studio.sh`             | Builds the front end and runs the studio on port 5080                                                                     |
| `scripts/setup-entra.sh`            | Creates the app registration, Dana and Luis, and their app roles                                                          |
| `scripts/teardown-entra.sh`         | Deletes exactly those Entra objects                                                                                       |
| `scripts/deploy-azure.sh`           | Previews (default) or provisions (`--apply`, billable) the hosted platform                                                |
| `scripts/seed-azure-sql.sh`         | Loads the seed CSVs into Azure SQL and grants the studio read access                                                      |
| `scripts/deploy-app.sh`             | Builds and deploys the studio to App Service                                                                              |
| `scripts/teardown-azure.sh`         | Deletes the demo resource group (stops all charges)                                                                       |
| `.github/workflows/deploy-demo.yml` | Manual GitHub Actions deploy through OIDC (no secrets)                                                                    |

## Prerequisites

* .NET SDK 10.0.100 or later (`global.json` rolls forward to the latest minor)
* Azure CLI, signed in with `az login`, for the agent
* Node.js and npm, for the front end and to regenerate the golden fixture

## Run the What-If Studio

```bash
./scripts/run-studio.sh
```

Open `http://localhost:5080`. The script builds the front end into the API's `wwwroot` and
runs the API in Development mode. Everything except chat works offline in CSV mode; chat
needs the Foundry deployment (see [Agent chat](#agent-chat)).

* Sign in as Dana (network-wide, approves) or Luis (Southeast only, proposes and executes).
  Before Entra is set up, pick a local dev persona at the top right instead.
* Change the diesel price with presets or sliders, and toggle playbook actions or levers.
  Outcomes recompute on the server with the same engine and scope rules as the agent.
* Ask the agent questions. Tool calls appear as chips. When the agent asks to log a
  decision, a card appears; nothing is logged until you click Confirm.
* Approve, reject, propose or execute from the actions table. Buttons follow the role.
* Decisions apply to one region at a time. Luis proposes and executes for the Southeast only.
  Dana picks the region with the "Decide for" tabs in Actions and decisions (each tab counts
  what is waiting there). Approving A2 for the Southeast changes only the Southeast's numbers;
  to roll it out, approve it for the Midwest and West too. In the whole-network view the
  outcomes apply each approval in its own region, and the sliders show what-ifs only.
* See what is waiting for you. A header badge counts the actions waiting on your role (Dana:
  proposals to approve or reject; Luis: approvals to execute). Those rows sort first and are
  highlighted, and the chat shows a "Waiting for you" banner with the buttons.
* Undo an approval with Revoke (finance, before execution, reason required). Click an
  approved chip under Impact levers to jump to its row. Executed actions stay final until
  `./scripts/reset-demo.sh`.
* Each action starts at its playbook setting, for example A6 shifts 3% of orders to Pickup.
  A1, A2, A4 and A6 can be adjusted. Luis can propose a different value, or change his own
  pending proposal. Dana can approve the proposed value or another one, and can change an
  approval until it is executed (type a value in "At __ %", then **Approve at…** or **Change
  to…**). In chat, use `/propose A6 5%` or `/approve A6 4% smaller pilot`. Every step logs
  the setting, and outcomes, savings, the guardrail check and execution results use it. A
  setting that breaks the Express guardrail cannot be approved. A3 and A5 are on/off.
* The playbook defaults and their sources live in `data/config/action-levers.csv`: A1-A3
  come from the reference demo, and A4-A6 are approximated from the playbook text. Edit the
  file and restart the studio to change them.
* Decide in the chat without relying on the model. Under each agent reply that names actions
  (A1-A6), decision buttons show each action's live status and your next steps. Or type a
  command; commands never go to the agent:

  | Command                                       | Effect                                                |
  |-----------------------------------------------|-------------------------------------------------------|
  | `/propose A1 [5%]`                            | Propose, optionally at another setting (regional ops) |
  | `/approve A1 [region] [5%] [reason]`          | Approve for a region, or change its setting (finance) |
  | `/reject A1 [region] <reason>`, `/revoke ...` | Reject, or withdraw an approval (finance)             |
  | `/execute A1`                                 | Execute an approved action (regional ops)             |
  | `/status [A1] [region]`                       | Status and your next steps                            |
  | `/help`                                       | List commands                                         |

  Without a region, commands use the region selected in Actions and decisions. Buttons and
  commands use the same server checks as the actions table.

For front-end development with hot reload, run the API and `npm run dev` in
`src/CostToServe.Web`, then open `http://localhost:5173`; Vite proxies `/api` to port 5080.

### Sign-in

```bash
./scripts/setup-entra.sh     # creates directory objects; asks you to type 'create'
./scripts/run-studio.sh      # now shows "Sign in with Microsoft"
```

The script creates the `cost-to-serve-demo` app registration (SPA sign-in, API scope,
app roles `FinanceAnalyst` and `RegionalOps`, assignment required) and the users
`dana.demo@<domain>` and `luis.demo@<domain>`. Temporary passwords go only to
`secrets/demo-users.local.txt` (git-ignored); each user changes it at first sign-in. It
assigns Dana FinanceAnalyst and Luis RegionalOps, and writes Luis's region (Southeast) and
the app IDs to git-ignored local settings. Requiring MFA is recommended. Re-running reuses
existing objects and never resets passwords; `./scripts/teardown-entra.sh` removes them.

| Signed-in role | Sees                   | Can                                  |
|----------------|------------------------|--------------------------------------|
| FinanceAnalyst | Whole network          | Approve or reject actions            |
| RegionalOps    | Configured region only | Propose and execute approved actions |
| No studio role | Nothing (403)          | -                                    |

Each action follows one workflow: Not reviewed → Proposed (regional ops) → Approved or
Rejected (finance) → Executed (regional ops). The actions table shows each action's status
and only the buttons valid for you now; out-of-order steps return HTTP 409. Approving applies
an action to the scenario outcomes. Executing marks it live and adds it to Execution results:
a modeled pilot readout (Southeast zones 1-3 versus control zones 4-7) with the weekly saving
if scaled. Reset the demo to start over.

Without Entra settings, the API falls back to local dev personas in Development only.

### API

Every endpoint except `/api/health` and `/api/auth-config` needs a caller: an Entra access
token (`Authorization: Bearer`) when Entra is configured, otherwise the `X-Demo-Persona`
header (`dana` or `luis`) in Development.

```bash
curl http://localhost:5080/api/health
curl -H 'X-Demo-Persona: luis' "http://localhost:5080/api/cost-per-order?deliveryType=Express"
curl -X POST http://localhost:5080/api/scenario -H 'X-Demo-Persona: dana' -H 'content-type: application/json' \
  -d '{"dieselShift":0.5,"durationWeeks":13,"actionIds":"A1,A2"}'
```

| Endpoint                                       | Purpose                                                                   |
|------------------------------------------------|---------------------------------------------------------------------------|
| `GET /api/health`                              | Data mode, seed version, chat configured, sign-in mode                    |
| `GET /api/auth-config`                         | Sign-in mode and public MSAL settings for the browser                     |
| `GET /api/me`, `GET /api/dashboard`            | Caller, scope, fuel market, cost per order, presets                       |
| `POST /api/scenario`                           | Diesel change with levers or `actionIds` over 13 weeks, plus guardrail    |
| `POST /api/guardrail`                          | Guardrail check for levers or actions                                     |
| `GET /api/cost-per-order`, `GET /api/variance` | Actual cost per order; week-over-week drivers                             |
| `GET /api/playbook`                            | Approved actions with modeled weekly savings and latest decision          |
| `GET /api/results`                             | Modeled pilot-vs-control results of executed actions                      |
| `GET /api/decisions`, `POST /api/decisions`    | Decision log; propose, approve, reject or execute                         |
| `POST /api/chat`                               | Agent chat as server-sent events: `delta`, `tool`, `confirmation`, `done` |
| `POST /api/confirmations/{id}`                 | Confirm or cancel a decision card; only its owner can                     |

Scope is enforced on the server: Luis gets 403 for other regions. Invalid input returns 400.

Levers and ranges match the reference sliders: `batch` 0-0.6, `closer` 0-0.4, `split` 0-0.6,
`pickup` 0-0.1, `fee` -1 to 3, `basket` and `cap` true or false. Diesel shift is -0.5 to 1.0
USD per gallon; duration is 1-13 weeks. Out-of-range input returns HTTP 400.

## Test

```bash
dotnet test CostToServe.slnx
npm test --prefix src/CostToServe.Web
```

* Parity tests feed the engine the reference HTML's embedded inputs and match 472 golden
  cases from the original JavaScript engine, to a relative tolerance of 1e-12.
* Anchor tests compute the demo numbers from `data/seed`:

| Anchor                                                  | Value from seed CSVs |
|---------------------------------------------------------|----------------------|
| Southeast Express cost per order, prior week            | $15.56               |
| Southeast Express cost per order, current week          | $17.01 (+9.3%)       |
| Rise attributed to fuel, route density, split shipments | 57%, 34%, 10%        |
| Diesel +$0.50/gal for 13 weeks, network-wide            | $28.2M added cost    |
| A1 batch 0.35 plus A2 closer store 0.20, same shock     | 88% offset           |
| Express within 3 hours with A1 plus A2                  | 84.25% (passes 80%)  |

The 88% offset is network-wide, against the +$0.50/gal, 13-week shock.

> [!NOTE]
> The seed CSVs store daily diesel to three decimals. Weekly diesel can therefore differ
> from the reference HTML's embedded values by up to $0.0006 per gallon. For example, the
> current-week Southeast Express cost is $17.0091 from the CSVs versus $17.0118 in the HTML.
> Both round to the anchors above.

To regenerate the golden fixture after the reference HTML changes:

```bash
node tools/extract-html-fixture.mjs
```

## Agent chat

The agent runs against a GPT-4.1-mini deployment in the demo's own resource group.

```bash
az login
./scripts/deploy-foundry.sh      # billable; creates or updates rg-cost-to-serve-demo
./scripts/chat.sh --as dana      # Director, Fulfillment Finance: network-wide, approves
./scripts/chat.sh --as luis      # Southeast Regional Delivery Ops: Southeast only, proposes and executes
./scripts/chat.sh --as dana --ask "What if diesel rises 50 cents for 13 weeks? Do A1 plus A2 offset it?"
```

`deploy-foundry.sh` writes the endpoint to `src/CostToServe.Api/appsettings.Local.json`
(git-ignored). You can also set `Foundry__Endpoint` and `Foundry__Deployment`. The account
accepts Entra tokens only; there are no keys.

| Tool                  | Purpose                                                     |
|-----------------------|-------------------------------------------------------------|
| `get_cost_per_order`  | Cost per order from daily facts                             |
| `explain_variance`    | Week-over-week change: fuel, route density, split shipments |
| `run_scenario`        | Diesel shock with levers or playbook actions, `action_ids`  |
| `check_guardrail`     | Express at least 80% within 3 hours, plus other guardrails  |
| `get_action_playbook` | Approved actions with modeled weekly savings                |
| `log_decision`        | Records a decision after you type `yes` to confirm          |

Personas are a local stand-in until Entra sign-in in Increment 4. Scope is enforced in the
tools, not the prompt: Luis gets `scope_denied` for other regions. Finance approves or
rejects; regional ops proposes and executes approved actions.

After each turn the terminal prints a span summary (`invoke_agent`, `chat`,
`execute_tool`, HTTP) with tokens and tool outcomes, but no prompt text. Add `--no-trace`
to hide it. To view full traces, set `OTEL_EXPORTER_OTLP_ENDPOINT`, for example with the
Aspire dashboard:

```bash
docker run --rm -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 ./scripts/chat.sh --as dana
```

## Azure resources and cost

Everything lives in one dedicated resource group, `rg-cost-to-serve-demo`. Nothing else in
the subscription is read or changed. Increment 2 deployed the Foundry account and the
`gpt-4.1-mini` deployment (30K TPM, default content filter), billed per token. Testing used
about 3,500-6,500 tokens per chat turn.

Increment 5 adds the hosted platform ([ADR 004](docs/adr/004-hosted-platform.md)):

```text
Browser -> APIM front gate -> App Service (studio, .NET 10, B2) -> APIM AI gateway -> Foundry
                                 | -> Azure SQL (seed cost facts)             |
                                 | -> Cosmos DB (decisions, chat transcripts) |
                                 '-> Application Insights (traces and logs, via managed identity)
```

Every service call uses managed identity; there are no keys or passwords. The App Service
accepts traffic only from APIM. Expect about $79-110 per month while the resources exist,
mostly APIM Developer (about $48) and the B2 plan (about $25). A $100 monthly budget emails
at 80% actual and 100% forecast spend.

```bash
./scripts/deploy-azure.sh             # read-only preview of what would be created
./scripts/deploy-azure.sh --apply     # billable; asks you to type 'deploy'; APIM takes 30-60 minutes
./scripts/seed-azure-sql.sh           # load seed CSVs into SQL (asks you to type 'seed')
./scripts/deploy-app.sh               # build and deploy the studio
./scripts/setup-entra.sh --redirect-uri https://<apim>.azure-api.net
```

The studio URL is `studioUrl` in `infra/azure-outputs.local.json` (git-ignored). For CI,
`.github/workflows/deploy-demo.yml` deploys on manual dispatch through GitHub OIDC; its
header lists the four environment variables to set.

To stop all charges, delete the resource group. This is destructive; the script lists the
resources and asks you to type the group name:

```bash
./scripts/teardown-azure.sh           # add --purge to free soft-deleted names for a redeploy
```

## Reset the demo

```bash
./scripts/reset-demo.sh          # asks you to type 'reset'
./scripts/reset-demo.sh --yes    # no prompt
```

Reset first verifies `data/seed` against `SHA256SUMS`, and refuses to continue if any seed
file changed. It then restores only the files it manages in `data/local`, including the
decision log and chat transcripts; other files are left alone.

`./scripts/reset-demo.sh --target azure` deletes every decision and chat transcript in the
demo's Cosmos DB; refresh the studio to see it. Seed facts in Azure SQL are not changed; reload
them with `./scripts/seed-azure-sql.sh`.

## Data provenance

`data/seed` holds the ten illustrative CSVs and their data dictionary, copied unchanged from
the reference data folder. The supplied ZIP is byte-identical apart from macOS metadata.
`docs/reference` holds the reference HTML pages. The reference workbook (`.xlsx`) carries a
"Confidential - Internal Only" sensitivity label, so it stays local and git-ignored; the same
data is in `data/seed` as CSVs. Each folder has a
`SHA256SUMS` manifest; check with `shasum -a 256 -c SHA256SUMS`.
