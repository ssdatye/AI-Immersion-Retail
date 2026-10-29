import { useState } from "react";
import { api } from "../api";
import type { ApiAuth } from "../auth";
import { toPercent, usdCompact } from "../format";
import type { Caller, DecisionRecord, ExecutionResults, Playbook, PlaybookAction } from "../types";
import { ExecutionResultsView } from "./ExecutionResultsView";

interface Props {
  auth: ApiAuth;
  caller: Caller;
  playbook: Playbook | null;
  decisions: DecisionRecord[];
  results: ExecutionResults | null;
  onChanged: () => void;
}

type Decision = "propose" | "approve" | "reject" | "execute";

const IMPACT_CLASS: Record<string, string> = { None: "lo", Low: "lo", "Low-Medium": "md", "Medium-Low": "md", "Medium-High": "hi" };
const STATUS_CLASS: Record<string, string> = {
  "Not reviewed": "st-none",
  Proposed: "st-proposed",
  Approved: "st-approved",
  Rejected: "st-rejected",
  Executed: "st-executed",
};
const PAST: Record<string, string> = { propose: "Proposed", approve: "Approved", reject: "Rejected", execute: "Executed" };

export const when = (iso: string) => {
  const d = new Date(iso);
  const time = d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  return d.toDateString() === new Date().toDateString() ? time : `${d.toLocaleDateString([], { month: "short", day: "numeric" })} ${time}`;
};

/** What the caller is waiting for when they have no step to take. */
function waitingHint(action: PlaybookAction, isFinance: boolean) {
  switch (action.status) {
    case "Executed":
      return "Done. Reset the demo to start over.";
    case "Proposed":
      return isFinance ? "" : "Waiting for Finance to approve.";
    case "Approved":
      return isFinance ? "Waiting for Ops to execute." : "";
    default:
      return "";
  }
}

export function ActionsPanel({ auth, caller, playbook, decisions, results, onChanged }: Props) {
  const [busy, setBusy] = useState<string | null>(null);
  const [feedback, setFeedback] = useState<{ actionId: string; text: string; error: boolean } | null>(null);
  const [rejecting, setRejecting] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  // Draft settings in percent, per action, until a decision is logged.
  const [drafts, setDrafts] = useState<Record<string, number>>({});
  const isFinance = caller.canApprove;
  const needsYou = (a: PlaybookAction) => (isFinance ? a.status === "Proposed" : a.status === "Approved");

  const decide = async (actionId: string, decision: Decision, why?: string, settingPct?: number) => {
    setBusy(actionId);
    setFeedback(null);
    try {
      await api(auth, "/api/decisions", {
        method: "POST",
        body: { actionId, decision, reason: why ?? null, setting: settingPct === undefined ? null : settingPct / 100 },
      });
      const at = settingPct === undefined ? "" : ` at ${settingPct}%`;
      setFeedback({ actionId, text: `✓ ${PAST[decision]}${at} by you just now.`, error: false });
      setRejecting(null);
      setReason("");
      setDrafts(({ [actionId]: _, ...rest }) => rest);
      onChanged();
    } catch (e) {
      setFeedback({ actionId, text: (e as Error).message, error: true });
    } finally {
      setBusy(null);
    }
  };

  const steps = [
    { n: 1, text: "Regional ops proposes", mine: !isFinance },
    { n: 2, text: "Finance approves or rejects", mine: isFinance },
    { n: 3, text: "Regional ops executes → results", mine: !isFinance },
  ];

  return (
    <section className="panel">
      <h2>
        Actions and decisions <span className="muted">· savings for {playbook?.savingsScope ?? "…"}</span>
      </h2>
      <ol className="workflow" aria-label="How decisions work">
        {steps.map((s) => (
          <li key={s.n} className={s.mine ? "mine" : ""}>
            <span className="step">{s.n}</span> {s.text}
            {s.mine && <span className="you">you</span>}
          </li>
        ))}
      </ol>
      <p className="muted small">
        Approved actions are applied to the <strong>Scenario outcomes</strong> right away; revoking removes them. Every step is logged with who
        and when. Savings are per action and not additive.
      </p>
      <table className="table actions-table">
        <colgroup>
          <col className="c-action" />
          <col className="c-saving" />
          <col className="c-status" />
          <col className="c-next" />
        </colgroup>
        <thead>
          <tr>
            <th>Action</th>
            <th className="num">Weekly saving</th>
            <th>Status</th>
            <th>Your next step</th>
          </tr>
        </thead>
        <tbody>
          {[...(playbook?.actions ?? [])]
            .sort((x, y) => Number(needsYou(y)) - Number(needsYou(x)))
            .map((a) => {
            const available = a.availableDecisions as Decision[];
            const rowFeedback = feedback?.actionId === a.actionId ? feedback : null;
            const spec = a.setting ?? null;
            const editable = spec !== null && (available.includes("propose") || available.includes("approve") || a.canAmendSetting);
            const current = spec ? toPercent(spec.value) : 0;
            const draft = spec ? (drafts[a.actionId] ?? current) : 0;
            const draftValid = spec !== null && Number.isFinite(draft) && draft >= toPercent(spec.min) && draft <= toPercent(spec.max);
            const changed = editable && draft !== current;
            // Proposing or approving sends the draft only when it differs; otherwise the server keeps the current setting.
            const settingFor = (d: Decision) => (changed && (d === "propose" || d === "approve") ? draft : undefined);
            const amendTo = a.canAmendSetting && changed ? (a.status === "Approved" ? "approve" : "propose") : null;
            return (
              <tr
                key={a.actionId}
                id={`action-row-${a.actionId}`}
                className={[rowFeedback && !rowFeedback.error ? "flash" : "", needsYou(a) ? "needs-you" : ""].join(" ").trim()}
              >
                <td>
                  {needsYou(a) && <span className="you-tag">Waiting for you</span>}
                  <strong>{a.actionId}</strong> {a.action}
                  <div className="muted small">
                    {a.effect}
                    {a.leverMapping.startsWith("derived") ? " · modeled approx." : ""}
                  </div>
                  {spec && (
                    <div className="small setting-line">
                      Setting: <strong>{spec.text}</strong>
                      <span className="muted"> · playbook {spec.isDefault ? "default" : `default ${toPercent(spec.default)}%`}</span>
                    </div>
                  )}
                  {a.leverSource && <div className="muted small">Default source: {a.leverSource}</div>}
                  <div className="action-tags">
                    <span className={`tag ${IMPACT_CLASS[a.customerImpact] ?? ""}`} title="Customer impact">
                      {a.customerImpact === "None" ? "No customer impact" : `${a.customerImpact} impact`}
                    </span>
                    <span className={`tag ${a.guardrailPassed ? "lo" : "hi"}`} title={a.guardrailRule}>
                      {a.guardrailPassed ? "✔ Guardrail ok" : "✖ Breaks guardrail"}
                    </span>
                  </div>
                </td>
                <td className="num">{usdCompact(a.estimatedWeeklySavingUsd)}</td>
                <td>
                  <span className={`status ${STATUS_CLASS[a.status] ?? ""}`}>{a.status}</span>
                  {a.latestDecisionBy && a.latestDecisionAt && (
                    <div className="muted small">
                      by {a.latestDecisionBy} · {when(a.latestDecisionAt)}
                    </div>
                  )}
                  {a.latestDecisionReason && <div className="muted small">“{a.latestDecisionReason}”</div>}
                </td>
                <td className="actions">
                  {rejecting === a.actionId ? (
                    <span className="reject-form">
                      <input
                        autoFocus
                        placeholder="Reason (required)"
                        value={reason}
                        onChange={(e) => setReason(e.target.value)}
                        onKeyDown={(e) => e.key === "Enter" && reason.trim() && void decide(a.actionId, "reject", reason.trim())}
                      />
                      <button className="secondary" disabled={!reason.trim() || busy !== null} onClick={() => decide(a.actionId, "reject", reason.trim())}>
                        {a.status === "Approved" ? "Revoke" : "Reject"}
                      </button>
                      <button className="link" onClick={() => setRejecting(null)}>
                        Cancel
                      </button>
                    </span>
                  ) : (
                    <>
                      {editable && spec && (
                        <label className="setting-input" title={`${spec.label}: ${toPercent(spec.min)}% to ${toPercent(spec.max)}%`}>
                          At{" "}
                          <input
                            type="number"
                            min={toPercent(spec.min)}
                            max={toPercent(spec.max)}
                            step={toPercent(spec.step)}
                            value={Number.isFinite(draft) ? draft : ""}
                            onChange={(e) => setDrafts((all) => ({ ...all, [a.actionId]: e.target.value === "" ? NaN : Number(e.target.value) }))}
                            aria-label={`${a.actionId} setting in percent`}
                          />
                          %
                        </label>
                      )}
                      {editable && spec && !draftValid && (
                        <div className="error small">
                          Use {toPercent(spec.min)}% to {toPercent(spec.max)}%.
                        </div>
                      )}
                      <div className="buttons">
                        {available.map((d) => (
                          <button
                            key={d}
                            className={d === "reject" ? "secondary" : "primary"}
                            disabled={busy !== null || (settingFor(d) !== undefined && !draftValid)}
                            onClick={() => (d === "reject" ? setRejecting(a.actionId) : decide(a.actionId, d, undefined, settingFor(d)))}
                            title={d === "reject" && a.status === "Approved" ? "Revoke the approval before it is executed" : undefined}
                          >
                            {busy === a.actionId
                              ? "…"
                              : d === "reject" && a.status === "Approved"
                                ? "Revoke"
                                : `${d[0].toUpperCase() + d.slice(1)}${settingFor(d) !== undefined ? ` at ${draft}%` : ""}`}
                          </button>
                        ))}
                        {amendTo && (
                          <button
                            className="primary"
                            disabled={busy !== null || !draftValid}
                            onClick={() => decide(a.actionId, amendTo, undefined, draft)}
                            title={a.status === "Approved" ? "Change the approved setting before it is executed" : "Change your proposal"}
                          >
                            {busy === a.actionId ? "…" : `Change to ${draft}%`}
                          </button>
                        )}
                      </div>
                      {available.length === 0 && <span className="muted small">{waitingHint(a, isFinance) || "No step for you."}</span>}
                      {available.length > 0 && waitingHint(a, isFinance) && <div className="muted small">{waitingHint(a, isFinance)}</div>}
                    </>
                  )}
                  {rowFeedback && <div className={rowFeedback.error ? "error small" : "ok small"}>{rowFeedback.text}</div>}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <ExecutionResultsView results={results} />
      <h3>Decision log</h3>
      {decisions.length === 0 ? (
        <p className="muted">No decisions yet.</p>
      ) : (
        <ol className="log">
          {decisions.map((d) => (
            <li key={d.timestamp + d.actionId + d.decision}>
              <span className="muted">{when(d.timestamp)}</span> <strong>{d.actionId}</strong> {PAST[d.decision]?.toLowerCase() ?? d.decision}
              {d.setting != null ? ` at ${toPercent(d.setting)}%` : ""} by {d.actor}
              {d.reason ? ` - "${d.reason}"` : ""}
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
