---
title: "ADR 005: Agent evaluations"
description: "Increment 6 decisions: where evaluations run, what is checked deterministically, the judge model, and project isolation."
ms.date: 2026-10-29
status: Accepted
---

## Context

Increment 6 adds a repeatable evaluation of the agent: 20 to 40 demo questions with expected tool calls and numbers, scored by evaluators, with a report. The demo's rule is that numbers come from tools, so the evaluation must check numbers against the cost engine, not only ask a model whether the answer looks right.

## Options considered

| Option | Pros | Cons | Effort | Risk |
|---|---|---|---|---|
| A: Run locally with an evaluation SDK; report in the repo | Simple; no extra Azure resources | No portal view for the customer story | Low | Low |
| B: A, plus the same rows evaluated in Foundry (chosen) | Portal charts and drill-down; same scores in both places | One extra Foundry project | Medium | Low |
| C: Foundry cloud evaluation of an agent target only | Fully managed | The agent is self-hosted (ADR 004), so Foundry cannot call it as a target; custom numeric and role checks are hard to express | High | Medium |

The owner chose B.

## Decision

* **Runner in .NET** (`evals/CostToServe.Evals`). It runs the studio's own agent code (instructions, tools, scope, model deployment) and records each tool call through an optional tool decorator added to `CostToServeAgentFactory` (the studio passes none). Expected numbers come from calling the same tools directly, so expectations track the engine.
* **Deterministic checks in the runner:** tool selection, numeric accuracy at the quoted precision, numbers from tools (no invented or self-calculated figures), and decision rules by role, region and setting. These need no model and are unit tested.
* **Foundry judges in an isolated project.** `proj-evals` is created under the demo's Foundry account by Bicep, with a system-assigned identity and no connections. `evals/foundry_eval.py` uses the `/openai/v1/evals` API through `curl` and the Azure CLI token (standard library only, as in the MafTracingPoc reference, and robust to TLS-inspecting proxies). It refuses to submit if the project has a connection. Built-in judges: task adherence, tool-call accuracy, intent resolution, groundedness. The runner's results ride along as `string_check` criteria so every score shows in the portal.
* **Judge model:** the existing `gpt-4.1-mini` deployment. A separate, stronger judge model was not deployed, to keep the demo at one model. Deployment capacity was raised from 30K to 150K tokens per minute; it is billed per token, so capacity has no fixed cost, and the app stays limited to 30K by the AI gateway.
* **Roles:** the developer gets Foundry User and Cognitive Services User on the Foundry account, in addition to Cognitive Services OpenAI User.

## Consequences

* The first runs found two real issues (multi-region approval without asking; subtracting two tool results) and one regression in the first fix; all three are fixed in the instructions. See [docs/evals/README.md](../evals/README.md).
* The tool-call accuracy judge scores down calls that the tools refuse by design. The report marks these as expected refusals instead of hiding the judge's result.
* Runs go to the model directly, not through APIM, because they run locally with the developer's sign-in; the agent code and model are the same.
* Run folders hold full answers and tool results and are git-ignored; only the generated report is committed. The Foundry portal link is printed, not saved, because it contains subscription and resource IDs.

## Revision (October 30, 2026)

The golden set was revised to 35 cases to match the demo as it now runs: the studio's question chips word for word, the "Ask the agent about these changes" message with its settings context, custom packages (ADR 007), what is waiting for Finance, a question without data (a fuel forecast) and a prompt-injection attempt. The runner gained a package store per case, package setup steps and a lever check for package proposals, and records a question blocked by the content filter as a refusal by policy. The revision found seven agent problems, fixed in the instructions or by returning the needed figure from a tool; see [docs/evals/README.md](../evals/README.md#revision-of-october-30-2026).
