import { describe, expect, it } from "vitest";
import { caseVerdict, cents, filterCases, money, policyNote, receiptSummary, tokens, toolChain, traceSteps } from "./trust";
import type { EvaluationCase, TraceSpan } from "./types";

describe("receiptSummary", () => {
  it("reports traced numbers, review items and answers without figures", () => {
    expect(receiptSummary({ quoted: 6, traced: 6, unsupported: [] })).toEqual({ tone: "ok", text: "6 of 6 numbers traced to the cost engine" });
    expect(receiptSummary({ quoted: 3, traced: 2, unsupported: ["$0.47"] }).tone).toBe("review");
    expect(receiptSummary({ quoted: 0, traced: 0, unsupported: [] }).tone).toBe("none");
  });
});

describe("policyNote", () => {
  it("prefers refusals, then decisions", () => {
    expect(policyNote([{ name: "get_cost_per_order", outcome: "scope_denied", message: "Southeast only" }])).toEqual({ tone: "refused", text: "Southeast only" });
    expect(policyNote([{ name: "log_decision", outcome: "ok", decision: "recorded" }]).tone).toBe("decision");
    expect(policyNote([{ name: "run_scenario", outcome: "ok" }]).tone).toBe("none");
    expect(policyNote([{ name: "content_safety", outcome: "blocked", message: "Blocked by Azure AI Content Safety" }])).toEqual({
      tone: "refused",
      text: "Blocked by Azure AI Content Safety",
    });
  });
});

describe("acceptance tests", () => {
  const base = { category: "facts", question: "q", answer: "a", tools: [], checks: {}, judges: null, numbers: [], unsupported: [], durationMs: 1 };
  const cases: EvaluationCase[] = [
    { ...base, id: "1", persona: "luis", result: "pass", refusalByDesign: false },
    { ...base, id: "2", persona: "luis", result: "pass", refusalByDesign: true },
    { ...base, id: "3", persona: "dana", result: "fail", refusalByDesign: false },
  ];

  it("reads a refusal by design as a pass, and a failure as a failure", () => {
    expect(caseVerdict(cases[0]).tone).toBe("ok");
    expect(caseVerdict(cases[1]).text).toBe("Passed · refused by design");
    expect(caseVerdict(cases[2]).tone).toBe("review");
  });

  it("filters by persona and by refusals", () => {
    expect(filterCases(cases, "all")).toHaveLength(3);
    expect(filterCases(cases, "luis").map((c) => c.id)).toEqual(["1", "2"]);
    expect(filterCases(cases, "refusals").map((c) => c.id)).toEqual(["2"]);
  });
});

describe("toolChain", () => {
  it("credits each number to the call it came from, in call order", () => {
    const chain = toolChain({
      tools: [
        { name: "get_action_playbook", outcome: "ok" },
        { name: "run_scenario", outcome: "ok" },
      ],
      numbers: [
        { text: "$9.4M", source: "run_scenario.fuelShockQuarterCostUsd" },
        { text: "$0.75", source: "your question" },
        { text: "106%", source: "run_scenario.offsetOfFuelShockPct" },
        { text: "$1.23", source: null },
        { text: "80%", source: "agent instructions" },
      ],
    });
    expect(chain.calls.map((c) => c.name)).toEqual(["get_action_playbook", "run_scenario"]);
    expect(chain.calls[1].numbers).toEqual([
      { text: "$9.4M", field: "fuelShockQuarterCostUsd" },
      { text: "106%", field: "offsetOfFuelShockPct" },
    ]);
    expect(chain.fromQuestion).toEqual(["$0.75"]);
    expect(chain.fromInstructions).toEqual(["80%"]);
    expect(chain.toReview).toEqual(["$1.23"]);
  });
});

describe("traceSteps", () => {
  it("adds up to the request total, with nested spans dropped and the rest as other", () => {
    const span = (kind: TraceSpan["kind"], startMs: number, durationMs: number) => ({ kind, label: kind, startMs, durationMs, error: false });
    const { steps, otherMs, totalMs } = traceSteps([
      span("request", 0, 5600),
      span("agent", 300, 5200),
      span("model", 300, 2100),
      span("tool", 2400, 3),
      span("model", 2403, 1900),
      span("person", 4400, 400),
      span("data", 4450, 50),
    ]);
    expect(steps.map((s) => s.kind)).toEqual(["model", "tool", "model", "person"]);
    expect(totalMs).toBe(5600);
    expect(steps.reduce((a, s) => a + s.durationMs, 0) + otherMs).toBe(totalMs);
  });
});

describe("cost formatting", () => {
  it("shows sub-cent costs with four decimals and larger ones with two", () => {
    expect(money(0.0026)).toBe("$0.0026");
    expect(money(0.114)).toBe("$0.11");
    expect(money(90.4)).toBe("$90.40");
    expect(money(0)).toBe("$0");
    expect(money(null)).toBe("–");
    expect(cents(0.003)).toBe("0.30¢");
    expect(tokens(13392)).toBe("13,392");
  });

  it("keeps each model call's tokens and cost on its trace step", () => {
    const { steps } = traceSteps([
      { kind: "request", label: "POST /api/chat", startMs: 0, durationMs: 4000, error: false },
      { kind: "model", label: "Model call", startMs: 10, durationMs: 1900, error: false, inputTokens: 6412, outputTokens: 31, costUsd: 0.0026 },
      { kind: "tool", label: "run_scenario", startMs: 1920, durationMs: 3, error: false, costUsd: 0 },
    ]);
    expect(steps.map((x) => [x.kind, x.inputTokens ?? null, x.costUsd])).toEqual([
      ["model", 6412, 0.0026],
      ["tool", null, 0],
    ]);
  });
});
