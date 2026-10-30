import { describe, expect, it } from "vitest";
import { leversAt, planIn, waitingFor } from "./pending";
import type { Caller, PlaybookAction, RegionDecision } from "./types";

const caller = (canApprove: boolean): Caller => ({
  displayName: canApprove ? "Dana" : "Luis",
  title: "",
  role: canApprove ? "FinanceAnalyst" : "RegionalOps",
  scope: "",
  canApprove,
});
const region = (name: string, status: RegionDecision["status"], value?: number): RegionDecision => ({
  region: name,
  status,
  availableDecisions: [],
  canAmendSetting: false,
  setting: value === undefined ? null : { lever: "closer", label: "", value, default: 0.2, min: 0.01, max: 0.4, step: 0.01, isDefault: false, text: "" },
});
const action = (actionId: string, ...byRegion: RegionDecision[]) => ({ actionId, action: actionId, byRegion }) as PlaybookAction;
const none = { batch: 0, closer: 0, split: 0, pickup: 0, fee: 0, basket: false, cap: false };

describe("waitingFor", () => {
  const actions = [
    action("A1", region("Southeast", "Proposed"), region("Midwest", "Approved")),
    action("A2", region("Southeast", "Approved")),
    action("A3", region("Southeast", "Not reviewed")),
  ];

  it("gives finance the proposals to decide, per region", () => {
    expect(waitingFor(caller(true), actions).map((a) => `${a.actionId}:${a.region}`)).toEqual(["A1:Southeast"]);
  });

  it("gives regional ops the approvals to execute, per region", () => {
    expect(waitingFor(caller(false), actions).map((a) => `${a.actionId}:${a.region}`)).toEqual(["A1:Midwest", "A2:Southeast"]);
  });
  it("gives finance a revision to decide, and regional ops waits for it", () => {
    const revised = { ...region("Southeast", "Approved", 0.2), pendingRevision: { value: 0.3, text: "30%", by: "Luis", at: "2026-10-30T07:00:00Z" } };
    const list = [action("A2", revised)];
    expect(waitingFor(caller(true), list).map((a) => a.pendingRevision?.text)).toEqual(["30%"]);
    expect(waitingFor(caller(false), list)).toEqual([]);
  });
});

describe("planIn", () => {
  it("keeps each approval in its own region at its decided setting", () => {
    const actions = [action("A2", region("Southeast", "Approved", 0.3), region("West", "Proposed", 0.2))];
    const plan = planIn(actions, ["Southeast", "West"], { A2: { ...none, closer: 0.2 } });
    expect(plan).toEqual([{ actionId: "A2", region: "Southeast", executed: false, levers: { ...none, closer: 0.3 }, settingText: "30%" }]);
    expect(planIn(actions, ["West"], {})).toEqual([]);
  });

  it("replaces only the adjustable lever", () => {
    expect(leversAt({ ...none, closer: 0.2, cap: true }, null)).toEqual({ ...none, closer: 0.2, cap: true });
  });
});
