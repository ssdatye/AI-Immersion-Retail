import type { AnswerEvidence, EvaluationCase, TraceSpan, TrustTurn } from "./types";

/** Refusal by policy: a tool refused (scope, role, workflow or guardrail) or the content filter blocked the request, not the model. */
export const isRefusal = (outcome: string) => outcome === "scope_denied" || outcome === "conflict" || outcome === "blocked";

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

/** Pass, refusal by design, or failed: how one acceptance test reads in the Trust Center. */
export function caseVerdict(c: Pick<EvaluationCase, "result" | "refusalByDesign">): { tone: "ok" | "expected" | "review"; text: string } {
  if (c.result !== "pass") {
    return { tone: "review", text: "Failed" };
  }
  return c.refusalByDesign ? { tone: "expected", text: "Passed · refused by design" } : { tone: "ok", text: "Passed" };
}

/** Status mark for one check or judge result. */
export const mark = (result: string | undefined) => (result === "pass" ? "✓" : result === "fail" ? "✗" : "–");

/** Filters the acceptance tests by persona or by refusals by design. */
export function filterCases(cases: EvaluationCase[], filter: string): EvaluationCase[] {
  if (filter === "refusals") {
    return cases.filter((c) => c.refusalByDesign);
  }
  return filter === "all" ? cases : cases.filter((c) => c.persona === filter);
}

export interface ChainCall {
  name: string;
  outcome: string;
  message?: string | null;
  decision?: string | null;
  /** Numbers in the answer that came from this call, with the field they came from. */
  numbers: { text: string; field: string }[];
}

/**
 * The answer's tool calls in the order the agent made them, each with the numbers it supplied.
 * A number is credited to the first call of that tool; numbers from the question or the
 * instructions, and numbers with no source, are returned separately.
 */
export function toolChain(e: Pick<AnswerEvidence, "tools" | "numbers">): {
  calls: ChainCall[];
  fromQuestion: string[];
  fromInstructions: string[];
  toReview: string[];
} {
  const calls: ChainCall[] = e.tools.map((t) => ({ ...t, numbers: [] }));
  const fromQuestion: string[] = [];
  const fromInstructions: string[] = [];
  const toReview: string[] = [];
  const seen = new Set<string>();
  for (const n of e.numbers) {
    if (seen.has(n.text)) {
      continue;
    }
    seen.add(n.text);
    if (!n.source) {
      toReview.push(n.text);
    } else if (n.source === "your question") {
      fromQuestion.push(n.text);
    } else if (n.source === "agent instructions") {
      fromInstructions.push(n.text);
    } else {
      const dot = n.source.indexOf(".");
      const tool = dot > 0 ? n.source.slice(0, dot) : n.source;
      const call = calls.find((c) => c.name === tool);
      if (call) {
        call.numbers.push({ text: n.text, field: dot > 0 ? n.source.slice(dot + 1) : n.source });
      } else {
        toReview.push(n.text);
      }
    }
  }
  return { calls, fromQuestion, fromInstructions, toReview };
}

export interface TraceStep {
  kind: TraceSpan["kind"];
  label: string;
  startMs: number;
  durationMs: number;
}

/**
 * Turns a trace into steps that add up: the model, tool, person and data spans (dropping any span that
 * sits inside another one), plus "other" for the rest of the request (gateway, history, evidence check,
 * framework). Steps plus other equal the total.
 */
export function traceSteps(spans: TraceSpan[]): { steps: TraceStep[]; otherMs: number; totalMs: number } {
  const request = spans.filter((s) => s.kind === "request").sort((a, b) => b.durationMs - a.durationMs)[0];
  const totalMs = request ? request.durationMs : Math.max(0, ...spans.map((s) => s.startMs + s.durationMs));
  const named = spans
    .filter((s) => s.kind === "model" || s.kind === "tool" || s.kind === "person" || s.kind === "data")
    .sort((a, b) => a.startMs - b.startMs);
  const inside = (s: TraceSpan, o: TraceSpan) =>
    o !== s && o.startMs <= s.startMs && o.startMs + o.durationMs >= s.startMs + s.durationMs && o.durationMs > s.durationMs;
  const steps = named
    .filter((s) => !named.some((o) => inside(s, o)))
    .map((s) => ({ kind: s.kind, label: s.label, startMs: s.startMs, durationMs: s.durationMs }));
  const used = steps.reduce((a, s) => a + s.durationMs, 0);
  return { steps, otherMs: Math.max(0, totalMs - used), totalMs: Math.max(totalMs, used) };
}

/** Milliseconds as "3 ms" or "1,900 ms". */
export const ms = (v: number) => `${Math.round(v).toLocaleString("en-US")} ms`;
