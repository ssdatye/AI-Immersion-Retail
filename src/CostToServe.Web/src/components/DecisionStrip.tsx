import { useState } from "react";
import { api, failureText } from "../api";
import type { ApiAuth } from "../auth";
import { toPercent } from "../format";
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

/** The server's answer to a decision: "recorded", or "already_recorded" when the same step was already in place. */
export interface DecisionOutcome {
  status: string;
  message?: string | null;
}

/** Posts a decision through the same server checks as the actions table. Throws with the server's message. */
export async function submitDecision(
  auth: ApiAuth,
  actionId: string,
  decision: DecisionVerb,
  reason?: string,
  setting?: number,
  region?: string,
): Promise<DecisionOutcome> {
  return api<DecisionOutcome>(auth, "/api/decisions", {
    method: "POST",
    body: { actionId, decision, reason: reason || null, setting: setting ?? null, region: region ?? null },
  });
}

/** An action in one region; decisions apply to one region at a time. */
export interface DecisionItem {
  actionId: string;
  region: string;
}

export const pastTense = (d: DecisionVerb) => PAST[d];

interface Props {
  auth: ApiAuth;
  items: DecisionItem[];
  playbook: Playbook | null;
  /** Show the region next to each action (when items span regions). */
  showRegion?: boolean;
  onDecided: () => void;
}

/**
 * Decision buttons for actions mentioned in an agent reply. Status and available buttons come
 * from the live playbook (server state), not from the model's text.
 */
export function DecisionStrip({ auth, items, playbook, showRegion, onDecided }: Props) {
  const [busy, setBusy] = useState(false);
  const [rejecting, setRejecting] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const [feedback, setFeedback] = useState<{ text: string; error: boolean } | null>(null);

  const rows = items.flatMap((item) => {
    const a = playbook?.actions.find((x) => x.actionId === item.actionId);
    const r = a?.byRegion?.find((x) => x.region === item.region);
    return a && r ? [{ key: `${item.actionId}:${item.region}`, a, r }] : [];
  });
  if (rows.length === 0) {
    return null;
  }

  const decide = async (actionId: string, region: string, decision: DecisionVerb, why?: string) => {
    setBusy(true);
    setFeedback(null);
    try {
      const r = await submitDecision(auth, actionId, decision, why, undefined, region);
      setFeedback({
        text: r.status === "already_recorded" && r.message ? `✓ ${r.message}` : `✓ ${actionId} ${PAST[decision].toLowerCase()} for ${region} by you.`,
        error: false,
      });
      setRejecting(null);
      setReason("");
    } catch (e) {
      setFeedback({ text: failureText(e), error: true });
    } finally {
      setBusy(false);
      // Always reload: after an error or a dropped connection the server may already have moved on.
      onDecided();
    }
  };

  return (
    <div className="decision-strip" aria-label="Decide on mentioned actions">
      {rows.map(({ key, a, r }) => {
        // A revision of an approved action needs a new setting: use the actions table or /propose A2 30%.
        const available = (r.availableDecisions as DecisionVerb[]).filter((d) => !(d === "propose" && r.status === "Approved"));
        return (
          <div key={key} className="decision-row">
            <span className="decision-id" title={a.action}>
              {a.actionId}
              {showRegion ? ` · ${r.region}` : ""}
            </span>
            <span className={`status ${STATUS_CLASS[r.status] ?? ""}`}>{r.status}</span>
            {r.setting && (
              <span className="muted small" title="Current setting. Change it in the actions table or with /approve A6 5%.">
                {r.setting.text}
              </span>
            )}
            {r.pendingRevision && <span className="revision small">revision: {toPercent(r.pendingRevision.value)}%</span>}
            {rejecting === key ? (
              <span className="decision-reason">
                <input
                  autoFocus
                  placeholder="Reason (required)"
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  onKeyDown={(e) => e.key === "Enter" && reason.trim() && void decide(a.actionId, r.region, "reject", reason.trim())}
                />
                <button className="secondary" disabled={busy || !reason.trim()} onClick={() => decide(a.actionId, r.region, "reject", reason.trim())}>
                  {r.pendingRevision ? "Decline" : r.status === "Approved" ? "Revoke" : "Reject"}
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
                  onClick={() => (d === "reject" ? setRejecting(key) : decide(a.actionId, r.region, d))}
                >
                  {d === "reject" && r.pendingRevision
                    ? "Decline revision"
                    : d === "approve" && r.pendingRevision
                      ? `Approve ${toPercent(r.pendingRevision.value)}%`
                      : d === "reject" && r.status === "Approved"
                        ? "Revoke"
                        : d[0].toUpperCase() + d.slice(1)}
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
