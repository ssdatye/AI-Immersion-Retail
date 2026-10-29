import { useState } from "react";
import { policyNote, receiptSummary, seconds, sourceLabel } from "../trust";
import type { AnswerEvidence } from "../types";

/**
 * Shown under each agent answer: every number in the answer traced to the tool result it came from,
 * plus any refusal by policy. Computed by the server after the answer, with the same check as the evaluation.
 */
export function EvidenceReceipt({ evidence }: { evidence: AnswerEvidence }) {
  const [open, setOpen] = useState(false);
  const summary = receiptSummary(evidence);
  const policy = policyNote(evidence.tools);
  const rows = evidence.numbers.filter((n, i, all) => all.findIndex((m) => m.text === n.text) === i);

  return (
    <>
      {policy.tone === "refused" && (
        <div className="receipt refused">
          <span className="rpill refused">🛡 Refused by policy</span> <strong>{policy.text}</strong>
          <span className="muted"> · enforced by the tool, not the model</span>
        </div>
      )}
      {(summary.tone !== "none" || policy.tone === "decision") && (
        <div className={`receipt ${summary.tone === "review" ? "review" : ""}`}>
          <div className="receipt-top">
            {summary.tone === "ok" && <span className="rpill ok">✓ Verified</span>}
            {summary.tone === "review" && <span className="rpill review">Review</span>}
            {summary.tone !== "none" && <strong>{summary.text}</strong>}
            {policy.tone === "decision" && <span className="rpill decision">{policy.text}</span>}
            <span className="muted">
              {" "}
              · {seconds(evidence.durationMs)}
              {evidence.traceId ? ` · trace ${evidence.traceId.slice(0, 8)}…` : ""}
            </span>
            {rows.length > 0 && (
              <button className="link small" onClick={() => setOpen(!open)}>
                {open ? "Hide evidence ▴" : "Show evidence ▾"}
              </button>
            )}
          </div>
          {open && (
            <table className="evidence-table">
              <thead>
                <tr>
                  <th>In the answer</th>
                  <th>Source</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((n) => (
                  <tr key={n.text}>
                    <td>{n.text}</td>
                    <td className={n.source ? "mono" : "mono review-text"}>{sourceLabel(n.source)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </>
  );
}
