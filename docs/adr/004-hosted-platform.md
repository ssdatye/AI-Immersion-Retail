---
title: "ADR 004: Hosted platform on Azure"
description: "Increment 5 decisions: front door, data in Azure SQL and Cosmos DB, AI gateway, deploy identity, monitoring, cost and shutdown."
ms.date: 2026-10-29
status: Accepted (provisioning pending approval)
---

## Context

Increment 5 hosts the What-If Studio on Azure in the dedicated resource group
`rg-cost-to-serve-demo` (ADR 000). It covers App Service B2 with Always On, one APIM
Developer instance hosting the front gate and the AI gateway (ADR 000, layout option 2),
Azure SQL serverless for cost facts, Cosmos DB serverless for decisions and transcripts, Key
Vault, and App Insights. Every call must use managed identity, with no keys or passwords.

The owner was unavailable when asked, so the recommended option is recorded for each
decision. Each can be changed before provisioning.

## Decisions

### Front door

| Option                                       | Pros                                                       | Cons                                                   | Effort | Risk       |
|----------------------------------------------|------------------------------------------------------------|--------------------------------------------------------|--------|------------|
| A: APIM fronts the whole site (chosen)       | One origin; every request traced and rate limited; no CORS | APIM in the path for static files; SSE needs care      | Low    | Low-medium |
| B: SPA from App Service, `/api` through APIM | Static files skip APIM                                     | Two origins, CORS, and a shared header secret for /api | Medium | Medium     |
| C: No front gate; APIM as AI gateway only    | Simplest                                                   | Drops the reference's front-gate policies              | Low    | Low        |

The studio API has an empty URL suffix, so `https://<apim>.azure-api.net/` serves the studio.
The App Service accepts traffic only from the APIM public IP (default action Deny). The
policy drops caller baggage, rate limits `/api/chat` (20 per minute per IP) and other API
calls (600 per minute per IP), and forwards without buffering so chat events stream. The
studio still validates the Entra token and app roles itself (ADR 003).

### Cost facts and mutable state

| Option                                                         | Pros                                               | Cons                                     | Effort | Risk   |
|----------------------------------------------------------------|----------------------------------------------------|------------------------------------------|--------|--------|
| A: SQL is the source; load into the engine at startup (chosen) | Same engine and numbers as CSV mode; fast what-ifs | Restart to pick up reloaded seed data    | Medium | Low    |
| B: Query SQL per request                                       | Always current                                     | Rewrites the data layer; slower what-ifs | High   | Medium |
| C: CSVs ship with the app; SQL is a reporting copy             | Simplest                                           | SQL is decorative                        | Low    | Low    |

* SQL schema `seed` mirrors the ten CSVs: one table per file, `row_num` plus one NVARCHAR
  column per CSV column holding the CSV text unchanged. The engine parses the same text in
  both modes, so the parity and anchor tests cover Azure mode. `seed.manifest` records the
  seed version shown in the studio.
* `seed-sql` (`./scripts/seed-azure-sql.sh`) runs as the developer, who is the server's
  Entra admin. It verifies `SHA256SUMS`, replaces the seed tables, reads them back, and
  grants the studio identity `SELECT` on schema `seed` only. The script opens a firewall
  rule for the developer's IP and removes it on exit.
* The database uses the free offer (`useFreeLimit`, auto-pause on exhaustion) and auto-pauses
  after 60 idle minutes. App Service Basic has no fixed outbound IP, so the server allows
  Azure-internal traffic; it accepts Entra tokens only (no SQL logins).
* Cosmos DB (serverless, local auth disabled): `decisions` (partition `/actionId`, the
  decision log including settings), `threads` (one item per chat turn, partition
  `/ownerId`), and `scenarios`, reserved for saved scenarios and evaluation runs. The
  studio caches decisions in memory and writes through, which is correct for one instance.
* CSV mode keeps working for local development. It now also writes chat turns to
  `data/local/chat_transcripts.jsonl`, and reset clears that file.

### Model path

| Option                                           | Pros                                             | Cons                                    | Effort | Risk       |
|--------------------------------------------------|--------------------------------------------------|-----------------------------------------|--------|------------|
| A: Studio to APIM AI gateway to Foundry (chosen) | Reference pattern; token limit; no keys anywhere | One more hop                            | Low    | Low-medium |
| B: Studio to Foundry directly                    | Simplest                                         | APIM unused for AI; no gateway controls | Low    | Low        |
| C: APIM with subscription keys in Key Vault      | Familiar                                         | A secret to rotate                      | Medium | Medium     |

The studio requests a Cognitive Services token with its user-assigned identity. APIM
validates it (`validate-azure-ad-token`: tenant, audience, and the studio's client ID only),
replaces it with APIM's own managed identity token, and applies `llm-token-limit`. The
studio's code is unchanged; only `Foundry:Endpoint` points at `https://<apim>/ai/`.

### Deploy identity

| Option                                                         | Pros                                           | Cons                            | Effort | Risk |
|----------------------------------------------------------------|------------------------------------------------|---------------------------------|--------|------|
| A: User-assigned identity with GitHub OIDC credential (chosen) | No app registration; no secret; in the demo RG | GitHub environment setup        | Low    | Low  |
| B: Entra app registration with federated credential            | Common pattern                                 | Another tenant object to manage | Low    | Low  |
| C: Deploy only from the developer's machine                    | Nothing to set up                              | No CI deploys                   | Low    | Low  |

The deploy identity trusts `repo:ssdatye/AI-Immersion-Retail:environment:demo` and has
Website Contributor on the studio web app only. The workflow runs on manual dispatch only.
`./scripts/deploy-app.sh` deploys the same package with the developer's sign-in.

### Monitoring

Application Insights has local auth disabled. The studio exports OpenTelemetry traces and
logs with its managed identity (Azure Monitor exporter 1.9.0, the newest compatible with the
pinned client libraries). APIM logs to the same resource through a logger that uses its
system-assigned identity, with W3C correlation and no bodies. Log Analytics has a 1 GB
daily cap.

### Deferred

Service Bus, the ingestion Function and Storage from the reference capture content
asynchronously for evaluations. They move to Increment 6, where the evaluation design
decides whether the `threads` container is enough.

## Resources

All in `rg-cost-to-serve-demo`, tagged `demo=walmart-cost-to-serve`. Names share one suffix.

| Resource                               | Configuration                                                        |
|----------------------------------------|----------------------------------------------------------------------|
| Foundry account and gpt-4.1-mini       | Existing from Increment 2; APIM identity gains OpenAI User           |
| API Management                         | Developer, 1 unit, system identity; `studio` and `ai-gateway` APIs   |
| App Service plan and web app           | Linux B2, .NET 10, Always On, ingress from APIM only, basic auth off |
| User-assigned identities               | `id-cts-web-*` (runtime), `id-cts-deploy-*` (GitHub OIDC)            |
| Azure SQL server and database          | Entra-only; serverless GP 1 vCore, free offer, auto-pause 60 minutes |
| Cosmos DB account                      | Serverless, local auth off; `decisions`, `threads`, `scenarios`      |
| Key Vault                              | RBAC, soft delete 7 days, no purge protection; no secrets yet        |
| Log Analytics and Application Insights | 30-day retention, 1 GB/day cap, local auth off                       |
| Budget                                 | 100 USD per month; email at 80% actual and 100% forecast             |

Role assignments are scoped to resources inside the group: OpenAI User (APIM on Foundry),
Monitoring Metrics Publisher (studio and APIM on App Insights), Cosmos Data Contributor
(studio, developer), Key Vault Secrets User (studio), Website Contributor (deploy identity
on the web app).

## Cost

East US 2 list prices from the Azure Retail Prices API, 730 hours per month, fetched
October 28 and 29, 2026. Ranges are planning assumptions, not quotes.

| Component                      | Basis                                        | USD per month |
|--------------------------------|----------------------------------------------|---------------|
| APIM Developer, 1 unit         | $0.0658 per hour                             | 48.03         |
| App Service B2 Linux           | $0.034 per hour                              | 24.82         |
| Azure SQL serverless           | Free offer; $0.5218 per vCore-hour beyond it | 0-5           |
| Foundry tokens (gpt-4.1-mini)  | Light demo use                               | 5-25          |
| Cosmos DB serverless           | Request units and storage                    | 1-3           |
| Log Analytics and App Insights | 1 GB/day cap                                 | 0-3           |
| Key Vault, identities, budget  | Negligible                                   | 0-1           |
| **Total**                      |                                              | **79-110**    |

APIM and the App Service plan account for about $73 of that and bill every hour they
exist, whether or not anyone uses the demo.

## Shutdown

* Pause between demos: nothing short of deletion stops APIM charges. SQL pauses itself.
* Stop everything: `./scripts/teardown-azure.sh` lists the group's resources, asks you to
  type the group name, and deletes `rg-cost-to-serve-demo`. `--purge` also purges the
  soft-deleted Key Vault, APIM and Foundry account so a redeploy can reuse the names.
* Redeploy later: `./scripts/deploy-azure.sh --apply`, then seed SQL and deploy the app.
* Entra objects are separate: `./scripts/teardown-entra.sh`.

## Consequences

* Provisioning is billable and needs the owner's explicit approval. The preview
  (`./scripts/deploy-azure.sh`) is read-only. It cannot expand modules whose inputs come
  from other modules' outputs (APIM, App Service, Cosmos, Key Vault, monitoring), so those
  are reviewed in this ADR rather than in the preview.
* After provisioning, the Entra app registration needs the studio URL as an SPA redirect
  URI (`./scripts/setup-entra.sh --redirect-uri https://<apim>.azure-api.net`).
* The decision cache assumes one App Service instance. Scaling out needs Cosmos reads per
  request or a change feed.
