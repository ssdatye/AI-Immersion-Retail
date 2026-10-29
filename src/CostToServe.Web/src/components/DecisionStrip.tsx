import { useState } from "react";
import { api } from "../api";
import type { ApiAuth } from "../auth";
import type { DecisionVerb } from "../commands";
import type { Playbook } from "../types";

const STATUS_CLASS: Record<string, string> = {
  "Not reviewed": "st-none",
  Proposed: "st-proposed",
  Approved: "st-approved",
  Rejected: "st-rejected",
  Executed: "st-executed",
};

const PAST: Record<DecisionVerb, string> = { propose: "Proposed", approve: "Approved", reject: "Rejected", execute: "Executed" };

/** Posts a decision through the same server checks as the actions table. Throws with the server's message. */
export async function submitDecision(auth: ApiAuth, actionId: string, decision: DecisionVerb, reason?: string, setting?: number) {
  await api(auth, "/api/decisions", { method: "POST", body: { actionId, decision, reason: reason || null, setting: setting ?? null } });
}

export const pastTense = (d: DecisionVerb) => PAST[d];

interface Props {
  auth: ApiAuth;
  actionIds: string[];
  playbook: Playbook | null;
  onDecided: () => void;
}

/**
 * Decision buttons for actions mentioned in an agent reply. Status and available buttons come
 * from the live playbook (server state), not from the model's text.
 */
export function DecisionStrip({ auth, actionIds, playbook, onDecided }: Props) {
  const [busy, setBusy] = useState(false);
  const [rejecting, setRejecting] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const [feedback, setFeedback] = useState<{ text: string; error: boolean } | null>(null);

  const actions = actionIds
    .map((id) => playbook?.actions.find((a) => a.actionId === id))
    .filter((a): a is NonNullable<typeof a> => a !== undefined);
  if (actions.length === 0) {
    return null;
  }

  const decide = async (actionId: string, decision: DecisionVerb, why?: string) => {
    setBusy(true);
    setFeedback(null);
    try {
      await submitDecision(auth, actionId, decision, why);
      setFeedback({ text: `✓ ${actionId} ${PAST[decision].toLowerCase()} by you.`, error: false });
      setRejecting(null);
      setReason("");
      onDecided();
    } catch (e) {
      setFeedback({ text: (e as Error).message, error: true });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="decision-strip" aria-label="Decide on mentioned actions">
      {actions.map((a) => {
        const available = a.availableDecisions as DecisionVerb[];
        return (
          <div key={a.actionId} className="decision-row">
            <span className="decision-id" title={a.action}>
              {a.actionId}
            </span>
            <span className={`status ${STATUS_CLASS[a.status] ?? ""}`}>{a.status}</span>
            {a.setting && (
              <span className="muted small" title="Current setting. Change it in the actions table or with /approve A6 5%.">
                {a.setting.text}
              </span>
            )}
            {rejecting === a.actionId ? (
              <span className="decision-reason">
                <input
                  autoFocus
                  placeholder="Reason (required)"
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  onKeyDown={(e) => e.key === "Enter" && reason.trim() && void decide(a.actionId, "reject", reason.trim())}
                />
                <button className="secondary" disabled={busy || !reason.trim()} onClick={() => decide(a.actionId, "reject", reason.trim())}>
                  {a.status === "Approved" ? "Revoke" : "Reject"}
                </button>
                <button className="link" onClick={() => setRejecting(null)}>
                  Cancel
                </button>
              </span>
            ) : available.length === 0 ? (
              <span className="muted small">no step for you now</span>
            ) : (
              available.map((d) => (
                <button
                  key={d}
                  className={d === "reject" ? "secondary" : "primary"}
                  disabled={busy}
                  onClick={() => (d === "reject" ? setRejecting(a.actionId) : decide(a.actionId, d))}
                >
                  {d === "reject" && a.status === "Approved" ? "Revoke" : d[0].toUpperCase() + d.slice(1)}
                </button>
              ))
            )}
          </div>
        );
      })}
      {feedback && <div className={feedback.error ? "error small" : "ok small"}>{feedback.text}</div>}
    </div>
  );
}
