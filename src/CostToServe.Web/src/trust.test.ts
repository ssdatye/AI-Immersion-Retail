import { describe, expect, it } from "vitest";
import { policyNote, receiptSummary } from "./trust";

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
