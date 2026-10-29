import type { AnswerEvidence, TrustTurn } from "./types";

/** Refusal by policy: the tool refused (scope, role, workflow or guardrail), not the model. */
export const isRefusal = (outcome: string) => outcome === "scope_denied" || outcome === "conflict";

/** Short, friendly name for a tool field path such as "explain_variance.drivers[0].sharePct". */
export function sourceLabel(source: string | null): string {
  if (!source) {
    return "not found in any tool result";
  }
  return source;
}

/** The headline of an evidence receipt. */
export function receiptSummary(e: Pick<AnswerEvidence, "quoted" | "traced" | "unsupported">): { tone: "ok" | "review" | "none"; text: string } {
  if (e.quoted === 0) {
    return { tone: "none", text: "No figures quoted" };
  }
  if (e.unsupported.length === 0) {
    return { tone: "ok", text: `${e.traced} of ${e.quoted} ${e.quoted === 1 ? "number" : "numbers"} traced to the cost engine` };
  }
  return { tone: "review", text: `${e.unsupported.length} of ${e.quoted} ${e.unsupported.length === 1 ? "number" : "numbers"} to review: not found in a tool result` };
}

/** One-line policy outcome for a turn: refusals, confirmations or guardrail. */
export function policyNote(tools: TrustTurn["tools"]): { tone: "ok" | "refused" | "decision" | "none"; text: string } {
  const refused = tools.find((t) => isRefusal(t.outcome));
  if (refused) {
    return { tone: "refused", text: refused.message ?? "Refused by policy" };
  }
  const decision = tools.find((t) => t.decision);
  if (decision?.decision === "recorded") {
    return { tone: "decision", text: "Logged after a person confirmed" };
  }
  if (decision?.decision === "not_recorded") {
    return { tone: "decision", text: "Declined by the user; nothing logged" };
  }
  return { tone: "none", text: "" };
}

export const seconds = (ms: number | null | undefined) => (ms == null ? "–" : `${(ms / 1000).toFixed(1)} s`);
