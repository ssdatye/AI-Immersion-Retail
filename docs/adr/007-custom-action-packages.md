---
title: "ADR 007: Custom action packages"
description: "How regional ops proposes a fine-tuned combination of levers to Finance as one named action."
ms.date: 2026-10-29
status: Accepted
---

## Context

Regional ops (Luis) can fine-tune every lever in the model, but could only propose the six playbook actions, A1 to A6, each at one setting. A combination he modeled, for example batch Express at 40% with the surcharge cap, had no way to reach Finance (Dana) as one proposal. The owner asked for a "packaged" custom action with a friendly, unique ID that Finance decides on as a whole.

## Options considered

| Option                                                          | Pros                                                                                           | Cons                                                                                   | Effort | Risk   |
|-----------------------------------------------------------------|------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------|--------|--------|
| A: Group existing proposals in the browser                      | No backend change                                                                              | Only A1 to A6 at their settings; Finance still decides each one; nothing is persisted  | Low    | Low    |
| B: Custom package as a first-class action (chosen)              | Any lever mix; one decision; same workflow, scope rules, guardrail and results as the playbook | New endpoint, tool and store; the agent has an eighth tool                             | Medium | Medium |
| C: Leave as is and propose separately                           | No change                                                                                      | The modeled combination is lost between Luis and Dana                                  | None   | Low    |

The owner chose B.

## Decision

* **A package is an action.** `ActionPackage` holds a name, one region, the levers, the creator and the time. It becomes a `DemoAction` with no adjustable setting, so the existing decision workflow (Propose, Approve or Reject, Execute), the guardrail check on approve and execute, the scenario plan and the pilot results apply to it without special cases.
* **Friendly IDs.** IDs are `P-<region code>-<sequence>`, for example `P-SE-01`, sequential per region; a conflicting write takes the next number.
* **Scope.** Only regional ops creates packages, and only for their own region. A package is visible only to callers who can see its region, and decisions on it apply only there.
* **Two ways in, one rule set.** `POST /api/packages` serves the model panel's **Propose as a package** form; the form itself is the confirmation. The agent's `propose_package` tool shows the usual confirmation card first. Both call the same method.
* **Storage.** Locally `data/local/packages.jsonl`; on Azure the existing Cosmos `scenarios` container under owner `packages`, so no infrastructure change. Reset clears packages with the decisions and transcripts.
* **Front end.** Packages show in Decisions with a 📦 tag, their levers and creator; chat commands and action buttons accept package IDs. The plan takes a package's levers from the playbook rows.

## Consequences

* Existing endpoints and their responses are unchanged; playbook rows gain optional package fields, and the chat's `tool` event can now name `propose_package`.
* The evaluation set (ADR 005) does not yet cover `propose_package`; re-run `./scripts/run-evals.sh` before relying on the summary for this tool.
* Packages cannot be edited; to change one, Finance rejects it and regional ops proposes a new one.
