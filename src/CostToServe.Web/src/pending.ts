import type { Caller, PlaybookAction } from "./types";

/**
 * Actions waiting for this caller: proposals for Finance to decide, and approvals for
 * regional ops to execute.
 */
export function waitingFor(caller: Caller, actions: PlaybookAction[]): PlaybookAction[] {
  return actions.filter((a) => (caller.canApprove ? a.status === "Proposed" : a.status === "Approved"));
}

export const waitingLabel = (caller: Caller, count: number) =>
  caller.canApprove ? `${count} to approve or reject` : `${count} ready to execute`;

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
