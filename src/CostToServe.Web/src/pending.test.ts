import { describe, expect, it } from "vitest";
import { waitingFor } from "./pending";
import type { Caller, PlaybookAction } from "./types";

const caller = (canApprove: boolean): Caller => ({
  displayName: canApprove ? "Dana" : "Luis",
  title: "",
  role: canApprove ? "FinanceAnalyst" : "RegionalOps",
  scope: "",
  canApprove,
});
const action = (actionId: string, status: PlaybookAction["status"]) => ({ actionId, status }) as PlaybookAction;

describe("waitingFor", () => {
  const actions = [action("A1", "Proposed"), action("A2", "Approved"), action("A3", "Not reviewed"), action("A4", "Executed")];

  it("gives finance the proposals to decide", () => {
    expect(waitingFor(caller(true), actions).map((a) => a.actionId)).toEqual(["A1"]);
  });

  it("gives regional ops the approvals to execute", () => {
    expect(waitingFor(caller(false), actions).map((a) => a.actionId)).toEqual(["A2"]);
  });
});
