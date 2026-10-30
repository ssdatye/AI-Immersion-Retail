import { describe, expect, it } from "vitest";
import { mentionedActions, parseCommand } from "./commands";

describe("parseCommand", () => {
  it("ignores ordinary messages", () => {
    expect(parseCommand("Approve A2 please")).toBeNull();
  });

  it("parses decisions with optional reasons", () => {
    expect(parseCommand("/approve a2 low customer impact")).toEqual({
      kind: "decision",
      decision: "approve",
      actionId: "A2",
      reason: "low customer impact",
    });
    expect(parseCommand(" /execute A1 ")).toEqual({ kind: "decision", decision: "execute", actionId: "A1", reason: "" });
  });

  it("maps revoke to reject and requires a reason", () => {
    expect(parseCommand("/approve A6 5% smaller pilot")).toEqual({
      kind: "decision",
      decision: "approve",
      actionId: "A6",
      reason: "smaller pilot",
      setting: 0.05,
    });
    expect(parseCommand("/propose a1 37.5%")).toEqual({ kind: "decision", decision: "propose", actionId: "A1", reason: "", setting: 0.375 });
    expect(parseCommand("/reject A1 50% too much")).toEqual({ kind: "decision", decision: "reject", actionId: "A1", reason: "50% too much" });
    expect(parseCommand("/approve a2 midwest 25% roll out")).toEqual({
      kind: "decision",
      decision: "approve",
      actionId: "A2",
      reason: "roll out",
      setting: 0.25,
      region: "Midwest",
    });
    expect(parseCommand("/status A2 west")).toEqual({ kind: "status", actionId: "A2", region: "West" });
    expect(parseCommand("/reject A2 Southeast too costly")).toMatchObject({ region: "Southeast", reason: "too costly" });
    expect(parseCommand("/revoke A1 stock risk")).toMatchObject({ kind: "decision", decision: "reject", reason: "stock risk" });
    expect(parseCommand("/reject A1")).toMatchObject({ kind: "invalid" });
    expect(parseCommand("/revoke A1")).toMatchObject({ kind: "invalid" });
  });

  it("rejects unknown commands and actions", () => {
    expect(parseCommand("/delete A1")).toMatchObject({ kind: "invalid" });
    expect(parseCommand("/approve A9")).toMatchObject({ kind: "invalid" });
    expect(parseCommand("/approve")).toMatchObject({ kind: "invalid" });
    expect(parseCommand("/status B2")).toMatchObject({ kind: "invalid" });
  });

  it("accepts custom package IDs", () => {
    expect(parseCommand("/approve p-se-01")).toMatchObject({ kind: "decision", decision: "approve", actionId: "P-SE-01" });
    expect(parseCommand("/execute P-SE-12")).toMatchObject({ kind: "decision", decision: "execute", actionId: "P-SE-12" });
    expect(parseCommand("/approve P-SE")).toMatchObject({ kind: "invalid" });
  });

  it("parses status and help", () => {
    expect(parseCommand("/status")).toEqual({ kind: "status", actionId: undefined });
    expect(parseCommand("/status a3")).toEqual({ kind: "status", actionId: "A3" });
    expect(parseCommand("/help")).toEqual({ kind: "help" });
  });
});

describe("mentionedActions", () => {
  it("finds unique playbook IDs in order", () => {
    expect(mentionedActions("A1 plus A2 offset 88%; A1 keeps the guardrail. A12 and BA3 are not IDs.")).toEqual(["A1", "A2"]);
    expect(mentionedActions("No actions here.")).toEqual([]);
    expect(mentionedActions("P-SE-01 bundles A1 and a fee; approve P-SE-01.")).toEqual(["P-SE-01", "A1"]);
  });
});
