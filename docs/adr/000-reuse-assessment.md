---
title: "ADR 000: Reference reuse assessment"
description: "Increment 0 assessment of the local MAF accountability reference for the illustrative cost-to-serve demo."
ms.date: 2026-10-28
status: "Reuse accepted; implementation choices pending"
---

## Status and recommendation

The user selected **B: copy a minimal working chain into AI-Immersion-Retail
and extend it**. Keep the reference unchanged. Frontend, backend, IaC and APIM
topology choices remain pending; this selection is not authorization to begin
Increment 1.

The reference provides .NET MAF orchestration, streamed tool activity, identity
binding, host-controlled confirmations, OpenTelemetry, and offline evaluation
plumbing. It does not provide the retail cost engine, required app roles,
Southeast data isolation, or What-If Studio.

The user has since authorized standing up every resource type the reference
deploys, in a **new dedicated resource group**, without reusing existing Azure
resources. See [Decision updates](#decision-updates). Retaining the AI gateway
keeps the reference model path (AgentApi to APIM to Foundry, with APIM managed
identity) instead of requiring a new direct-model adapter.

## Scope and provenance

* Target branch: `feat/cost-to-serve-demo`
* Requested reference: `ssdatye/MafTracingPoc`, `feat/unified-accountability`
* Assessed commit: `186ce5bb5241743b19b39b3fb9e003e46e8f257b`
* Local source root: `/Users/sarangdatye/repos/Open Mic Series/Telemetry and Eval/MafTracingPoc-Parallel/source-unified`
* Snapshot state: clean working tree on the requested branch; no remote configured
* Assessment date: October 28, 2026

The named branch was absent from GitHub during this assessment. Git remote-ref
lookup returned no matching branch, and the GitHub branch API returned 404.
The user supplied the local parent directory; its `source-unified` child
contains the requested branch. This is a local snapshot assessment, not a claim
that the commit is published or identical to current GitHub `main`.

Source citations below are relative to that local source root, with one-based
line numbers. Package versions are manifest declarations, not verified restore
results. The application, reference test suites, deployment scripts, and cloud
evaluations were not executed. This is not a full security or Git-history audit.

Existing unrelated target-workspace deletions and untracked files were left
untouched. No application source or reference assets were changed.

## Local reference asset inventory

The directory name is `Reference `, including its trailing space. Always quote
the complete path in shell commands.

| Expected asset                               | Observed state                        |
|----------------------------------------------|---------------------------------------|
| `walmart-v3-cost-to-serve-demo.html`         | Present                               |
| `walmart-v3-cost-to-serve-demo-data.zip`     | Added later; matches extracted folder |
| `walmart-v3-cost-to-serve-demo-data.xlsx`    | Present                               |
| `walmart-v3-cost-to-serve-architecture.html` | Present                               |
| `walmart-v3-cost-to-serve-demo-data/`        | Present, 10 CSVs and README           |

Other entries are `walmart-v3-exec-battlecards-INTERNAL.html`,
`walmart-v3-isd-onepager-INTERNAL.md`, `walmart-v3-research.md`,
`walmart-v3-s1-inventory-storyboard.png`,
`walmart-v3-s2-agent-commerce-storyboard.png`,
`walmart-v3-s4-engineering-storyboard.png`, and
`walmart-v3-visual-story.html`. These additional files were inventoried, not
selected for copying or assessed as business evidence.

The extracted data directory contains:

* `README.md`
* `action_playbook.csv`
* `carrier_invoices_weekly.csv`
* `cost_per_order_daily.csv`
* `decision_log.csv`
* `fuel_outlook_weekly.csv`
* `fuel_prices_daily.csv`
* `market_events.csv`
* `orders_daily.csv`
* `routes_daily.csv`
* `zones.csv`

Its README explicitly labels the data as fabricated, not Walmart data.
It describes 42 days, three regions, 20 zones, four delivery types, and a
13-week outlook. The HTML also labels the demo and personas as illustrative.
Public-company statements embedded in its narrative were not fact-checked and
must not be treated as validated customer claims.

No assets have been copied. After approval, the two selected HTML files and
XLSX belong in `docs/reference/`; seed CSVs and their README belong in
`data/seed/`. The ZIP was initially missing, so a source decision was recorded:

| Option                        | Pros                             | Cons                                | Effort | Risk                              |
|-------------------------------|----------------------------------|-------------------------------------|--------|-----------------------------------|
| Use extracted CSVs and README | Already available; no conversion | Cannot prove identity to absent ZIP | Low    | Low-medium provenance uncertainty |
| Supply the original ZIP       | Matches requested extraction     | Blocks copying until supplied       | Low    | Low after inventory verification  |
| Export workbook sheets        | Uses an available source         | Types, precision, schema can drift  | Medium | Higher conversion risk            |

The user approved using the extracted CSVs and README to establish the demo
baseline. The user then added the ZIP. Its 10 CSVs and README are
byte-identical to the extracted folder (`diff -rq`); its only extra entries are
macOS `__MACOSX` metadata, which must not be copied. Either source yields the
same baseline. Record file hashes when copying. Do not modify the originals.
Copying still waits for the Increment 1 go.

The user also requires demo-reset scripts. Include reset support in the
implementation plan, rather than relying on manual edits: restore the
deterministic baseline and reset mutable demo state as appropriate to each
increment. Keep original/baseline assets unchanged. Obtain explicit approval
before destructive reset execution, and avoid touching unrelated data.
Choose the reset interface and local/cloud scope before implementation.

## Reference implementation

### Structure and packages

| Area                | Observed implementation                                          | Source evidence                                         |
|---------------------|------------------------------------------------------------------|---------------------------------------------------------|
| Runtime             | ASP.NET Core/.NET 10; SDK 10.0.100 with latestMinor roll-forward | `global.json:1-6`, `Directory.Build.props`              |
| Backend             | AgentApi; typed options; MAF agent and HTTP endpoints            | `src/AgentApi/AgentApi.csproj:1-38`, `Program.cs:17-50` |
| Frontend            | Vite vanilla JavaScript; MSAL; browser Application Insights      | `src/Web/package.json:1-25`                             |
| Evaluation services | EvaluationContracts, EvaluationIngestion, PreviewRegistration    | `MafTracingPoc.sln`, `Directory.Packages.props:54-67`   |
| Evaluation scripts  | Python scripts using REST/Azure CLI; not backend runtime         | `AGENTS.md:35-41`, `evals/README.md:1-156`              |
| Supporting assets   | APIM policies, Cosmos assets, KQL, workbook, deployment scripts  | `apim/`, `cosmos/`, `kql/`, `monitoring/`, `scripts/`   |

Declared versions in `Directory.Packages.props:16-67`:

| Package                              | Declared version | Assessment                         |
|--------------------------------------|------------------|------------------------------------|
| Microsoft.Agents.AI                  | 1.22.0           | Reuse requires compatibility check |
| Microsoft.Agents.AI.OpenAI           | 1.22.0           | Keep aligned with MAF core         |
| Microsoft.Extensions.AI.Abstractions | 10.10.0          | Central package management         |
| OpenAI                               | 2.13.0           | Explicit compatibility pin         |
| System.ClientModel                   | 1.15.0           | Part of the client transport chain |
| Azure.AI.Projects.Agents             | 3.0.0-beta.3     | Isolated preview registration      |

No claim of current package freshness or vulnerability-free dependencies is
made. The source documents version-sensitive integration and the OpenAI pin
in `Directory.Packages.props:20-36` and `README.md:82-90`. Restore, compile,
supported-runtime checks, and dependency review are later gates.

### Request path, model access, and tools

The reference path is:

```text
Vite SPA -> front APIM -> AgentApi -> AI gateway APIM -> Foundry model
```

`README.md:14-25`, `src/AgentApi/Agents/Options.cs:13-47`, and
`src/AgentApi/Program.cs:124-143` describe the gateway boundary.
The factory uses an OpenAI-compatible client and custom HTTP transport,
not an already implemented direct Foundry managed-identity connection
(`src/AgentApi/Agents/AgentFactory.cs:17-52`).

Gateway authentication supports subscription-key and Entra modes; the tracked
default is subscription-key mode. APIM uses managed identity toward Cognitive
Services (`apim/ai-gateway-policy.xml:8,44`). Removing APIM requires replacing
gateway authentication/endpoint handling and verifying model-call tracing.

The frontend posts to `/api/chat` and consumes SSE-style `event:`/JSON `data:`
frames. Event types include `tool`, `host_action`, `final`, and `error`
(`src/Web/src/chatApiClient.js:16-107,113-177`;
`src/AgentApi/Endpoints/ChatEventStream.cs:11-12,60-89`).

UTC clock registration is visible in the agent factory
(`src/AgentApi/Agents/AgentFactory.cs:63-86`). Separate synthetic
transfer/accountability tools and host confirmations exist, but that does not
prove that every such tool is registered in the factory's model-visible list.
There are no retail tools or cost-engine registrations.

The SPA runs separately in the documented development/deployment workflow.
Startup configures CORS; same-app static-file serving is not implemented in the
reviewed startup. Serving the Vite build from the retail API is new packaging
work (`scripts/README.md:23-25,54-61,72-88`;
`src/AgentApi/Program.cs:145-177`).

### Identity and authority

Baseline mode is anonymous. Accountability mode enables JWT bearer
authentication and binds caller identity from validated token claims
(`src/AgentApi/Program.cs:35-50`;
`src/AgentApi/Services/CallerIdentity.cs:1-56`).
Front APIM validation and App Service ingress restrictions form part of the
reference's deployment boundary (`scripts/README.md:54-61,99-111`).

The reference scope is `Finance.Access`, not the requested app roles.
Its same-user confirmation is not independent approval
(`Architecture/architecture.md:363-364`;
`Architecture/security-threat-model.md:198-199`).
Host-rendered confirmation, rather than a model statement, changes authority
state (`AGENTS.md:24-27`;
`Architecture/code-walkthrough.md:450-496`).

For retail, enforce role and region scope on the server before reading data,
executing tools, or returning results. UI filtering and model instructions are
not authorization. Bind decisions to authenticated actors and immutable
scenario inputs; recheck guardrails when approving or executing an action.
Dana/Luis accounts, roles, approval permissions, and assignments do not exist
in the assessed reference.

### Observability, storage, and configuration

The reference propagates W3C context and uses server-minted turn identifiers.
Application Insights is intended for operational metadata; interaction content
is routed separately through bounded Service Bus ingestion to a Function and
Cosmos. It includes KQL queries and a workbook
(`README.md:14-36`; `src/AgentApi/Program.cs:52-120`;
`Architecture/architecture.md:180-198,246-263`).

Startup rejects sensitive GenAI content capture in production/accountability
mode. Baggage handling does not promote arbitrary inbound baggage to trusted
attributes (`src/AgentApi/Telemetry/ConversationBaggageProcessor.cs:7-35`).
Retain these privacy and correlation principles without automatically copying
the entire asynchronous content-storage topology.

Typed settings are validated at startup (`src/AgentApi/Program.cs:17-33`).
Reviewed tracked appsettings use placeholders/non-secret defaults; secret
configuration is supplied at runtime
(`src/AgentApi/Agents/Options.cs:38-47`;
`src/AgentApi/appsettings.json:1-30`).
Development uses generated environment files that deployment documentation
says are ignored (`scripts/README.md:72-86`).

### Tests, CI, and infrastructure

Tests exist for API/ingestion behavior, frontend behavior, Python evaluation
scripts, and browser smoke flows. No test counts, coverage percentages, or
passing results are asserted here because suites were not executed.

CI defines Python offline tests, .NET build/test, and SPA lint/build/test
(`.github/workflows/ci.yml:14-43`).
The reference warns that offline tests do not establish live Azure behavior
(`AGENTS.md:35-41`). Its nightly evaluation workflow is manual-only pending
identity/data-access setup; it is not evidence of scheduled successful runs.

Deployment is imperative PowerShell/Azure CLI, not Bicep or Terraform. It
provisions App Service, two APIM boundaries, Foundry, Service Bus, Cosmos,
storage, an ingestion Function, managed identities, and monitoring
(`scripts/README.md:45-70`;
`scripts/1_deploy_azure.ps1`;
`scripts/lib/MafTracingPoc.Deployment.psm1`).
Accountability setup remains manually layered (`scripts/README.md:89-111`).
Source CI is present; an application GitHub Actions deployment is missing.

## Fit against the requested demo

Present means a reusable implementation exists, not that it has been run here.

| Requirement                             | Fit     | Required adaptation or missing behavior                           |
|-----------------------------------------|---------|-------------------------------------------------------------------|
| Lightweight What-If Studio              | Partial | Vite chat exists; fuel panel, levers, outcomes and views do not   |
| No Teams dependency                     | Present | Browser/API path available; omit Teams-like reference mockups     |
| MAF backend                             | Present | .NET chain; replace sample domain and register retail tools       |
| One Linux B2 app, Always On             | Partial | App Service exists; B2, static serving and settings need IaC      |
| GPT-4.1/mini via MI                     | Partial | Reuse APIM AI gateway MI path; new model deployment and settings  |
| Model content filters enabled           | Partial | Content-capture guards exist; deployment filter policy unverified |
| CSV-only local data mode                | Missing | Explicit deterministic repository and local state needed          |
| Azure SQL serverless cost facts         | Missing | Schema, ingestion, MI access and query implementation needed      |
| Cosmos threads/scenarios/decision log   | Partial | Content/ledger patterns exist; retail model and isolation needed  |
| get_cost_per_order and explain_variance | Missing | Port engine and defined comparison semantics                      |
| run_scenario and all seven levers       | Missing | Port and test deterministic engine; no model-generated totals     |
| check_guardrail, Express >=80% <=3h     | Missing | Deterministic check with boundary tests                           |
| log_decision, approve/reject            | Partial | Host confirmation pattern; retail permissions/audit model absent  |
| FinanceAnalyst and RegionalOps roles    | Missing | Entra app roles, token policies and server enforcement            |
| Luis restricted to Southeast            | Missing | Data/tool/state access isolation, including forged filters        |
| Dana/Luis demo users and MFA guidance   | Missing | Separate approved identity work; no users created                 |
| App Insights and OTel                   | Present | Reuse sanitized spans, correlation and tool activity              |
| Declarative IaC                         | Missing | Parameterized Bicep/Terraform and reviewed deployment             |
| GitHub Actions deployment               | Missing | OIDC, environment approval and deployment workflow                |
| Domain evaluations                      | Partial | Export/submission patterns exist; domain dataset/scoring absent   |
| Fallback, warm-up, 8-step runbook       | Missing | Label fallback; never silently pass it off as live model output   |

## Evaluation readiness

### Reusable evaluation pieces

The reference exports Cosmos interactions as JSONL and submits offline Foundry
evaluations (`evals/5_export_dataset.py:1-104`;
`evals/6_run_foundry_eval.py:1-77`).
Configured examples include relevance, coherence, and intent resolution
(`evals/6_run_foundry_eval.py:48-56`;
`evals/README.md:136-140`).

Exports retain scenario, actor, tool facts, confirmations, and optional lineage.
However, the messages format contains user/assistant messages only; tool facts
and confirmations are JSON-encoded metadata, not native tool-call/result
messages (`evals/accountability_evidence.py:44-78`).
That schema needs adaptation before tool-aware evaluation.

The existing regression gate is synthetic keyword/regex checking, not numeric
or cost-domain scoring (`evals/regression/golden.jsonl:1`;
`evals/7_run_regression.py`).
No Azure AI Evaluation SDK dependency was found in the reviewed manifests.
The evaluation path is an explicit offline operation, not a per-chat judge.

### Missing evaluators and acceptance evidence

| Evaluation           | Reusable starting point           | Missing work                                                            |
|----------------------|-----------------------------------|-------------------------------------------------------------------------|
| Groundedness         | Response and bounded source facts | Supply explicit tool-result context; configure supported evaluator      |
| Tool-call accuracy   | Tool facts/activity               | Expected names, arguments, region scope, call order/results as needed   |
| Task adherence       | Intent-resolution example         | Separate task-adherence checks; not equivalent to relevance             |
| Numeric accuracy     | Deterministic host-fact pattern   | Independent engine fixtures, units, tolerances, rounding and provenance |
| Authorization        | Caller identity/host confirmation | Dana/Luis cross-region and approval-denial cases                        |
| Guardrail compliance | No retail implementation          | 80% boundary, prohibited combinations and approval-time revalidation    |

Azure AI Evaluation SDK/Foundry evaluation support is feasible, but not
ready-to-run for this domain. Confirm current SDK/API compatibility and
supported evaluator schemas before choosing a runner. Keep model-judged
evaluations offline and explicitly approved because they can incur charges.

The 20-30-question dataset should record question, actor/role, data version,
expected tool calls/arguments, authoritative result, expected decision or
denial, tolerances, and observed trace/turn identifiers. Include all levers,
negative fuel shifts, date/zone scopes, guardrail boundaries, and rejected
actions. Generate expected answers from reviewed fixtures, not from the
model response or the same unverified output being evaluated.

### Local numeric reconnaissance

Only the reviewed deterministic functions from the original HTML were
executed locally in a Node VM, with its embedded JSON data. No UI, model,
network call, or Azure evaluator was executed by that check.

| Requested anchor                      | Observed result                      |
|---------------------------------------|--------------------------------------|
| Southeast Express prior week CPO      | $15.5620772194                       |
| Southeast Express spike CPO           | $17.0117671307                       |
| Week-over-week CPO increase           | 9.3155296104%                        |
| +$0.50/gal for 13 weeks, network      | $28,234,887.1791 added cost          |
| A1 batch 0.35 + A2 closer 0.20 offset | 88.1237703260% of that network shock |
| Same levers, Express within 3 hours   | 84.25%; no guardrail warnings        |

The 88% anchor is specifically the combined levers applied across the network
against the +$0.50/gal, 13-week shock. It is not a Southeast weekly
before/after percentage. Tests and UI labels must preserve the denominator.
This reconnaissance is not the ported module or its future unit-test suite.

Source evidence: original HTML `unit`, `weekly`, `scenario`, `guard` at
lines 197-240; A1/A2 definitions at lines 283-284.
`plan` and `respond` at lines 424-446 are scripted chat behavior, not a real
agent. They must not be presented as live model reasoning.

Important semantics to preserve or explicitly decide later:

* `weekly` uses week-six volumes; historical variance uses separate week-five/six inputs.
* Scenario duration controls fuel-shock weeks, while lever effects extend across a 13-week quarter.
* The Express service percentage is an illustrative modeled estimate, not observed delivery telemetry.
* The engine clamps portions of surcharge formulas; the data README simplifies these formulas.
* Authoritative tools must distinguish cost, net fee/incentive impact, and percentage-offset denominators.

## Quality, licensing, and risk

| Risk                          | Evidence and consequence                                                            | Response before reuse                                          |
|-------------------------------|-------------------------------------------------------------------------------------|----------------------------------------------------------------|
| Version-sensitive integration | MAF/OpenAI pin and compatibility notes; preview registration package                | Restore/build/test selected chain; omit unused preview project |
| Authentication boundary       | Anonymous baseline; APIM forms part of protected deployment                         | Explicit API JWT/role enforcement without relying on APIM      |
| Authority mismatch            | Same-user transfer confirmation, not retail approval semantics                      | Define proposal, finance approval and ops execution separately |
| Data/evaluation mismatch      | User/assistant export plus metadata, no cost oracle                                 | Domain schemas and deterministic evaluator fixtures            |
| Infrastructure cost           | Two APIM boundaries, queue, Function, storage                                       | User authorized full topology; choose APIM layout vs budget    |
| Secret-handling caveats       | Runtime key mode and placeholder API key in Options.cs:38-47,86-96                  | Prefer MI; ignore local secret files; no placeholder fallback  |
| Hardcoded environment IDs     | Workbook embeds a resource ID at monitoring/evaluation-workbook.template.json:19-25 | Parameterize; never copy subscription or tenant identifiers    |
| Telemetry volume/privacy      | 100% POC sampling; separate content lane                                            | Bounded sanitized tracing, retention and ingestion controls    |
| Deployment drift              | Imperative scripts; manual accountability steps                                     | Declarative IaC, reviewable parameters and OIDC deployment     |
| License obligations           | MIT license, LICENSE:1-22; no reviewed third-party notice file                      | Retain source notices; review dependency redistribution terms  |

No actual credential values were observed in the reviewed tracked
source/config/templates/scripts. This is not proof of absence across all
files or history. GitHub secret references are configuration references, not
embedded secret values. Private operational artifacts, credential files, and
parent-directory Azure configuration were not inspected.

Content-capture suppression is not model content filtering. The reviewed
source does not establish that the deployed model has the requested filters;
that must be explicit and verified in a later approved deployment.

## Reuse decision candidates

Effort is relative, not a delivery-time commitment.

| Option                          | Pros                                            | Cons                                              | Effort | Risk                                      |
|---------------------------------|-------------------------------------------------|---------------------------------------------------|--------|-------------------------------------------|
| A: extend reference in place    | Most existing plumbing retained                 | Changes read-only reference; inherits extra stack | Medium | High scope coupling and deployment drift  |
| B: copy minimal chain to target | Preserves source; reuses transport/auth/tracing | Must remove gateway assumptions and domain code   | Medium | Medium, bounded through integration tests |
| C: patterns-only clean rebuild  | Small domain-first codebase; clean boundaries   | Recreates streaming, tracing, identity and evals  | High   | Medium-high integration regression risk   |

Recommend B because the user's target is a separate repository and the most
valuable existing work is cross-cutting infrastructure behavior, not retail
business logic. Copying should be selective, preserve MIT notices, and retain
provenance at the assessed commit. Do not copy generated settings, identifiers,
operational evidence, sample transfer behavior, or the full Azure footprint.
Option A also needs explicit permission to lift the reference's read-only rule.

## Candidate implementation decisions

All recommendations below remain proposals. Obtain the user's choice before
scaffolding or making these non-trivial implementation decisions.

### Backend

| Option                      | Pros                                  | Cons                                    | Effort | Risk                                 |
|-----------------------------|---------------------------------------|-----------------------------------------|--------|--------------------------------------|
| .NET 10 ASP.NET Core + MAF  | Matches reference; most pattern reuse | C# port of JS engine; direct MI adapter | Medium | Medium version/adapter compatibility |
| Python ASGI + MAF           | Convenient CSV/evaluation ecosystem   | Rewrites reference runtime integration  | High   | Medium-high parity risk              |
| Split .NET API/Python agent | Language flexibility                  | Two runtimes; extra tracing/deployment  | High   | High complexity for one small demo   |

Recommend .NET with a supported runtime/package compatibility check before
implementation. Evaluation tooling may remain offline Python without a second
production service.

### Frontend

| Option            | Pros                                       | Cons                                   | Effort     | Risk                        |
|-------------------|--------------------------------------------|----------------------------------------|------------|-----------------------------|
| Vite + React      | Component model suits stateful studio      | New framework and auth/chat adaptation | Medium     | Low-medium integration risk |
| Vite + Preact     | Small bundle; component-based UI           | Compatibility details; less reuse      | Medium     | Medium ecosystem risk       |
| Vite + vanilla JS | Matches reference; fewest new dependencies | Manual shared state and view lifecycle | Low-medium | Medium UI-state complexity  |

Recommend Vite + React for scenario comparison, synchronized levers, chat,
role-specific views, and approval state. Reuse transport and telemetry patterns
rather than assuming the reference DOM code is directly reusable. Recommend
vanilla JS instead only if the user prioritizes minimal dependency/port effort.
Serve the selected Vite production build from the backend.

### Infrastructure and deployment

| Option                  | Pros                                    | Cons                                  | Effort        | Risk                        |
|-------------------------|-----------------------------------------|---------------------------------------|---------------|-----------------------------|
| Bicep + GitHub OIDC     | Azure-native; no external state backend | New declarative templates required    | Medium        | Low-medium API/version risk |
| Terraform + GitHub OIDC | Reviewable plans; reusable modules      | State backend and provider management | Medium-high   | Medium state/provider risk  |
| Azure CLI/PowerShell    | Closest to existing deployment scripts  | Imperative drift; not requested IaC   | Low initially | High repeatability risk     |

Recommend Bicep plus GitHub OIDC for this Azure-only demo. Resource creation,
identity creation, and role assignments require separate explicit approvals.
No deployment workflow or infrastructure has been generated.

## Azure authentication and cost feasibility

Interactive `az login` succeeded. The user confirmed the displayed subscription
for later increments. Account, subscription and tenant identifiers are
intentionally not persisted here. Confirmation does not authorize billable
actions or identity/role changes. No subscription resource was provisioned.

The architecture HTML's approximately $76/month estimate is a supplied
illustration, not a verified full quote. A public Azure Retail Prices API query
on the assessment date returned this primary-meter price:

| Service                    | Region  | USD per hour | 730-hour month |
|----------------------------|---------|--------------|----------------|
| App Service Basic B2 Linux | East US | $0.034       | $24.82         |

Pricing source: [Azure Retail Prices API](https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview).
Filter: `serviceName eq 'Azure App Service' and armRegionName eq 'eastus' and skuName eq 'B2' and priceType eq 'Consumption'`.
The Linux primary meter was selected, not the non-primary Windows meter.
Python's request failed local certificate validation; the retry used
system-trusted curl without disabling certificate validation.
East US is a comparison assumption from the supplied estimate, not an approved
deployment-region choice.

This leaves $75.18 of a $100 planning envelope for SQL compute/storage,
Cosmos operations/storage, model tokens and evaluations, Key Vault, telemetry,
network charges and contingency. Those prices and usage assumptions are not
validated here; a full estimate is a gate before provisioning.
SQL active hours, model selection, evaluation runs, and telemetry can exhaust
the envelope. Serverless is not synonymous with zero idle/storage cost.
Budgets alert; they do not enforce a hard spending cap.

The user has since authorized the reference APIM/queue/Function stack. Its
cost impact and the APIM layout decision are in
[Decision updates](#decision-updates). Content filters and MI permissions still
need verification in the selected region/subscription.

For eventual shutdown: stopping the web app alone does not stop App Service
plan charges. Pause eligible SQL compute, stop model/evaluation traffic, and
account for retained storage/log costs. Full removal of dedicated demo
resources is destructive and must be explicitly approved; preserve required
data first. The exact commands and resource inventory belong in the future
runbook, not an unreviewed deployment script.

## Increment 0 verification and next gate

Completed checks:

* Listed the quoted reference folder and verified the extracted seed filenames.
* Verified local source branch, commit and clean state without modifying it.
* Inspected source, package declarations, configuration, CI, deployment and evaluation code.
* Executed original deterministic engine functions locally and reconciled all four requested anchors.
* Signed in interactively and obtained subscription confirmation without provisioning.
* Created only this assessment document on the requested target branch.

There is no new app to run. Review this ADR and the recorded numeric results.
No reference build, test suite, cloud evaluation, asset copy, commit or push was
performed. Documentation-only changes do not require application tests.

Reuse option B and the extracted seed baseline are approved. Before
Increment 1, obtain backend/frontend choices and agree on the reset interface.
Confirm the IaC choice and APIM layout before generating deployment assets.
Wait for an explicit go; do not begin the next increment automatically.

## Decision updates

Recorded October 28, 2026, after the initial assessment.

### Dedicated resource group and isolation guardrails

The user directed that the demo must not reuse existing Azure resources and must
not touch any existing resource group in the subscription. All demo resources go
into one new, dedicated resource group. The user authorized every resource type
that the reference deploys, including APIM, the AI gateway, Cosmos DB and
Application Insights. Increment 5 provisioning still needs a final go, because
it is billable.

Guardrails for every later increment:

* Create one new resource group with a name that does not already exist; check only that name.
* Scope every resource deployment to that group. Use subscription scope only to create the group.
* Do not list, read, modify, redeploy or delete resources in any other resource group.
* Create a new Foundry account, model deployment, APIM, Cosmos account and monitoring resources; reuse none.
* Scope role assignments to resources inside the new group, never to the subscription or other groups.
* Use a unique name suffix, because soft-deleted Foundry, APIM and Key Vault names can collide.
* Tag all resources, for example `demo=walmart-cost-to-serve`, for cost tracking and shutdown.
* Copy no names, endpoints, IDs or settings from the reference's deployment outputs.

Some limits are subscription-wide even with full resource-group isolation:

* Foundry model quota (TPM) is shared per subscription and region. Check available quota read-only before deployment.
* Entra app registrations and users are tenant objects, not resource-group resources. They still need separate approval.

### Reference topology to stand up

The reference deployment script provisions the following, with the SKUs it
uses (`scripts/lib/MafTracingPoc.Deployment.psm1:340,376,455,493,549,631,641,662`;
`scripts/README.md:40-61`):

| Resource                     | Reference SKU or mode                | Retail demo adjustment                   |
|------------------------------|--------------------------------------|------------------------------------------|
| Log Analytics + App Insights | PerGB2018, 30-day retention          | Keep                                     |
| Foundry AIServices + model   | S0; GlobalStandard deployment        | GPT-4.1 or GPT-4.1-mini; content filters |
| Foundry projects             | Telemetry project and isolated evals | Keep separation for evaluations          |
| Front APIM                   | Developer, 1 unit                    | Layout pending (see options)             |
| AI gateway APIM              | Developer, 1 unit                    | Layout pending (see options)             |
| Service Bus + queue          | Basic                                | Keep for async content capture           |
| Cosmos DB                    | Serverless                           | Add retail threads, scenarios, decisions |
| Storage + Function           | Standard_LRS; Flex Consumption       | Keep for ingestion Function              |
| App Service plan + web app   | B1 Linux, .NET 10                    | B2 with Always On, as requested          |
| Azure SQL                    | Not in reference                     | Add serverless for cost facts            |
| Key Vault                    | Not in reference                     | Add for runtime secrets                  |
| Azure Monitor workbook       | Template with hardcoded resource ID  | Parameterize; never copy the ID          |

The reference APIM policies use `validate-jwt` (accountability revision),
`cors`, `rate-limit-by-key`, `authentication-managed-identity` and
`llm-token-limit` (`apim/application-api-policy.xml:27,56,59`;
`apim/ai-gateway-policy.xml:44,58`). Microsoft Learn lists `llm-token-limit` for
Developer, Basic, Basic v2, Standard, Standard v2, Premium and Premium v2, not
Consumption ([llm-token-limit reference](https://learn.microsoft.com/azure/api-management/llm-token-limit-policy)).

### APIM layout options

Prices are public Azure Retail Prices API list prices for East US 2, fetched
October 28, 2026, using 730 hours per month. The APIM Developer unit meter is
marked non-primary in the API, so treat it as indicative.

| Option                                    | APIM $/month | Pros                                         | Cons                                                  | Effort     | Risk       |
|-------------------------------------------|--------------|----------------------------------------------|-------------------------------------------------------|------------|------------|
| 1: Two Developer instances (as reference) | about $96    | Exact reference topology; separate gateways  | Pushes the total well above $100; two slow provisions | Low        | Low        |
| 2: One Developer instance, two APIs       | about $48    | All policies kept; same hop chain; half cost | One shared instance, identity and failure domain      | Low-medium | Low-medium |
| 3: Consumption tier                       | near $0      | Lowest cost; fast provisioning               | No `llm-token-limit`; policy rewrites; cold starts    | Medium     | Medium     |

Developer tier has no SLA and suits demos. APIM cannot be stopped, so it bills
every hour it exists. Developer provisioning can take 30 minutes or more.

I recommend **option 2**. It keeps every reference policy and the traced chain
(SPA to APIM to AgentApi to APIM to Foundry) and keeps the demo closer to the
budget. The front-gate and AI-gateway APIs stay separate inside one instance,
each with its own policies and subscriptions. Choose option 1 if two physical
gateways matter more than the $100 target.

### Revised cost estimate

| Component                                 | Basis                                         | Option 1 $/month | Option 2 $/month |
|-------------------------------------------|-----------------------------------------------|------------------|------------------|
| App Service B2 Linux                      | $0.034/hour, list                             | 24.82            | 24.82            |
| APIM Developer                            | $0.0658/hour per unit, list                   | 96.07            | 48.03            |
| Azure SQL serverless GP, 1 vCore          | $0.5218/vCore-hour active; auto-pause         | 5-15             | 5-15             |
| Foundry tokens                            | Light demo use; GPT-4.1-mini cheaper than 4.1 | 5-25             | 5-25             |
| Cosmos DB serverless                      | Request units and storage                     | 1-5              | 1-5              |
| Log Analytics + App Insights              | Ingestion beyond any free allowance           | 0-5              | 0-5              |
| Service Bus, Function, Storage, Key Vault | Low volume                                    | 1-4              | 1-4              |
| **Approximate total**                     |                                               | **133-175**      | **85-127**       |

Ranges are planning assumptions, not quotes. SQL active hours, model choice,
evaluation runs and telemetry volume drive the spread. Azure SQL's free
serverless offer may lower SQL cost; eligibility must be verified first.

With the full reference topology, the ≈$100/month target holds only with
option 2 and light usage. Ways to stay under it:

* Use GPT-4.1-mini for chat; reserve GPT-4.1 for a comparison run.
* Keep SQL auto-pause on with the shortest practical delay.
* Run evaluations in planned batches, never on the request path.
* Between demo seasons, delete the dedicated resource group and redeploy from IaC. This is destructive and needs explicit approval each time.

A cost budget with alerts on the new resource group is recommended. Budgets
alert; they do not cap spending.

### Shutdown summary

Only deleting the dedicated resource group stops all charges, because APIM and
the App Service plan bill while they exist. SQL compute pauses automatically,
but storage still bills. Soft-deleted Foundry, APIM and Key Vault resources
may need purging before their names can be reused. The runbook will list exact
commands, scoped to the new resource group only.

### Provisional selections

The user was unavailable when asked to choose, so the recommended options are
recorded as provisional defaults. The user can override any of them before the
affected increment starts.

| Decision    | Provisional default                                                            | Applies from |
|-------------|--------------------------------------------------------------------------------|--------------|
| Backend     | .NET 10 ASP.NET Core + MAF, matching the reference                             | Increment 1  |
| Frontend    | Vite + React, built and served by the backend                                  | Increment 3  |
| Demo reset  | Backend CLI command with confirmation; admin UI button optional in Increment 7 | Increment 1  |
| APIM layout | Option 2: one Developer instance hosting both APIs                             | Increment 5  |
| IaC         | Bicep + GitHub OIDC                                                            | Increment 5  |

The user gave the go for Increment 1 on October 28, 2026, keeping these defaults.
