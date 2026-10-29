import { describe, expect, it } from "vitest";
import { caseVerdict, filterCases, policyNote, receiptSummary } from "./trust";
import type { EvaluationCase } from "./types";

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
