import { useId, useState } from "react";
import { isRefusal, policyNote, receiptSummary, seconds, toolChain } from "../trust";
import type { AnswerEvidence } from "../types";

interface Props {
  evidence: AnswerEvidence;
  /** Opens the Trust Center, where the step-by-step timing of the latest answer is shown. */
  onOpenTrust?: () => void;
}

/**
 * The proof under an agent answer: a one-line summary that expands to the tool calls in the order the agent
 * made them, the numbers each call supplied, and refusals or decisions. Computed by the server after the
 * answer with the same check as the evaluation.
 */
export function EvidenceReceipt({ evidence, onOpenTrust }: Props) {
  const [open, setOpen] = useState(false);
  const id = useId();
  const summary = receiptSummary(evidence);
  const policy = policyNote(evidence.tools);
  const chain = toolChain(evidence);
  const calls = evidence.tools.length;
  const refused = policy.tone === "refused";
  const label = [
    refused ? "🛡 Proof" : summary.tone === "review" ? "Proof" : "✓ Proof",
    calls === 0 ? "no tool calls" : `${calls} tool ${calls === 1 ? "call" : "calls"}${calls > 1 ? ", chained" : ""}`,
    refused
      ? "refused by policy"
      : summary.tone === "none"
        ? "no figures quoted"
        : summary.tone === "ok"
          ? `${evidence.traced} of ${evidence.quoted} numbers traced`
          : summary.text,
  ].join(" · ");

  return (
    <div className={`proof-box ${refused ? "refused" : summary.tone === "review" ? "review" : ""}`}>
      {refused && (
        <p className="proof-refusal">
          <strong>Refused by policy:</strong> {policy.text} <span className="muted">· enforced by the tool, not the model</span>
        </p>
      )}
      <button type="button" className="proof-toggle" aria-expanded={open} aria-controls={id} onClick={() => setOpen(!open)}>
        <span>{label}</span>
        {policy.tone === "decision" && <span className="rpill decision">{policy.text}</span>}
        <span className="arrow" aria-hidden="true">
          ▸
        </span>
      </button>
      <div id={id} className="proof-body" hidden={!open}>
        {calls > 0 && (
          <>
            <h4>Tool calls, in order</h4>
            <ol className="calls">
              {chain.calls.map((c, i) => (
                <li key={i} className="call">
                  <div className="call-head">
                    <span className="call-num" aria-hidden="true">
                      {i + 1}
                    </span>
                    <code>{c.name}</code>
                    <span className={`rpill ${isRefusal(c.outcome) ? "refused" : c.outcome === "ok" ? "ok" : "review"}`}>
                      {c.outcome === "ok" ? "✓ ok" : `🛡 ${c.outcome}`}
                    </span>
                    {c.decision && (
                      <span className="rpill decision">
                        {c.decision === "recorded" ? "logged after you confirmed" : "declined, nothing logged"}
                      </span>
                    )}
                  </div>
                  {c.outcome !== "ok" && c.message && <p className="small">{c.message}</p>}
                  {c.numbers.length > 0 ? (
                    <table className="evidence-table">
                      <thead>
                        <tr>
                          <th>Number in the answer</th>
                          <th>Field it came from</th>
                        </tr>
                      </thead>
                      <tbody>
                        {c.numbers.map((n) => (
                          <tr key={n.text}>
                            <td>{n.text}</td>
                            <td className="mono">{n.field}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  ) : (
                    <p className="small muted">No numbers from this call are quoted in the answer.</p>
                  )}
                </li>
              ))}
            </ol>
          </>
        )}
        {chain.fromQuestion.length > 0 && <p className="small">From your question: {chain.fromQuestion.join(", ")}</p>}
        {chain.fromInstructions.length > 0 && <p className="small">From the agent's rules: {chain.fromInstructions.join(", ")}</p>}
        {chain.toReview.length > 0 && (
          <p className="small review-text">Not found in a tool result, to review: {chain.toReview.join(", ")}</p>
        )}
        <p className="small muted">
          Answered in {seconds(evidence.durationMs)}
          {evidence.traceId ? ` · trace ${evidence.traceId.slice(0, 8)}` : ""}
          {onOpenTrust && (
            <>
              {" · "}
              <button type="button" className="link-inline" onClick={onOpenTrust}>
                Step-by-step timing in the Trust Center
              </button>
            </>
          )}
        </p>
      </div>
    </div>
  );
}
