import type { ActionSetting, ActionStatus, Caller, Levers, PendingRevision, PlaybookAction } from "./types";

/** An action waiting for this caller in one region. */
export interface WaitingItem {
  actionId: string;
  action: string;
  region: string;
  status: ActionStatus;
  setting?: ActionSetting | null;
  pendingRevision?: PendingRevision | null;
  latestDecisionBy?: string | null;
  latestDecisionAt?: string | null;
}

/** True when the caller has to act: Finance decides proposals and revisions; regional ops executes approvals with no revision pending. */
export const waitsFor = (caller: Pick<Caller, "canApprove">, r: { status: ActionStatus; pendingRevision?: PendingRevision | null }) =>
  caller.canApprove ? r.status === "Proposed" || !!r.pendingRevision : r.status === "Approved" && !r.pendingRevision;

/**
 * Actions waiting for this caller, per region: proposals and revisions for Finance to decide, and
 * approvals for regional ops to execute.
 */
export function waitingFor(caller: Caller, actions: PlaybookAction[]): WaitingItem[] {
  return actions.flatMap((a) =>
    (a.byRegion ?? [])
      .filter((r) => waitsFor(caller, r))
      .map((r) => ({ actionId: a.actionId, action: a.action, ...r })),
  );
}

export const waitingLabel = (caller: Caller, count: number) =>
  caller.canApprove ? `${count} to approve or reject` : `${count} ready to execute`;

/** An approved or executed action in one region: part of that region's plan. */
export interface PlanItem {
  actionId: string;
  region: string;
  executed: boolean;
  levers: Levers;
  settingText: string | null;
}

/** The plan in the given regions. Each action applies only in the region it was approved for. */
export function planIn(actions: PlaybookAction[], regions: string[], defaults: Record<string, Levers>): PlanItem[] {
  return actions.flatMap((a) =>
    (a.byRegion ?? [])
      .filter((r) => regions.includes(r.region) && (r.status === "Approved" || r.status === "Executed"))
      .map((r) => ({
        actionId: a.actionId,
        region: r.region,
        executed: r.status === "Executed",
        levers: leversAt(defaults[a.actionId], r.setting),
        settingText: r.setting ? `${Math.round(r.setting.value * 1000) / 10}%` : null,
      })),
  );
}

/** An action's levers at a decided setting (the setting replaces its one adjustable lever). */
export function leversAt(defaults: Levers | undefined, setting?: ActionSetting | null): Levers {
  const base = defaults ?? { batch: 0, closer: 0, split: 0, pickup: 0, fee: 0, basket: false, cap: false };
  return setting ? { ...base, [setting.lever]: setting.value } : base;
}

/** Scrolls to an action's row in the actions table and highlights it briefly. */
export function focusAction(actionId: string) {
  const row = document.getElementById(`action-row-${actionId}`);
  if (!row) {
    return;
  }
  row.scrollIntoView({ behavior: "smooth", block: "center" });
  row.classList.remove("focus");
  void row.offsetWidth;
  row.classList.add("focus");
}
