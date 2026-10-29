import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import type { ApiAuth } from "../auth";
import { isRefusal, policyNote, seconds } from "../trust";
import type { Caller, TrustCheck, TrustData } from "../types";
import { when } from "./ActionsPanel";

interface Props {
  auth: ApiAuth;
  caller: Caller;
  /** Whether the tab is showing; it refreshes only while visible. */
  visible: boolean;
  /** Bumped when a chat answer's evidence arrives, to refresh at once. */
  refreshKey: number;
}

const pct = (n: number, d: number) => (d > 0 ? Math.round((n / d) * 100) : 0);

function Bar({ check, refusals }: { check: TrustCheck; refusals?: number }) {
  const applicable = check.pass + check.fail;
  const expected = Math.min(refusals ?? 0, check.fail);
  const realFail = check.fail - expected;
  return (
    <div className="tbar">
      <span>{check.label}</span>
      <div className="track">
        <div className="fill" style={{ width: `${pct(check.pass, applicable)}%` }} />
        {expected > 0 && <div className="fill expected" style={{ width: `${pct(expected, applicable)}%` }} />}
        {realFail > 0 && <div className="fill failed" style={{ width: `${pct(realFail, applicable)}%` }} />}
      </div>
      <span className={`rpill ${realFail > 0 ? "review" : expected > 0 ? "expected" : "ok"}`}>
        {expected > 0 ? `${check.pass} + ${expected} ⓘ` : `${check.pass} / ${applicable}`}
      </span>
    </div>
  );
}

/**
 * The Trust Center: why the agent's answers can be trusted. The latest evaluation run, this session's
 * live evidence for the signed-in user's own answers, how the last answer was produced, and the
 * controls in force. Everything shown comes from the server; nothing here is hard-coded results.
 */
export function TrustCenter({ auth, caller, visible, refreshKey }: Props) {
  const [data, setData] = useState<TrustData | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    api<TrustData>(auth, "/api/trust").then(
      (d) => {
        setData(d);
        setError(null);
      },
      (e: Error) => setError(e.message),
    );
  }, [auth]);

  useEffect(() => {
    if (!visible) {
      return;
    }
    load();
    const timer = setInterval(load, 5000);
    return () => clearInterval(timer);
  }, [visible, load, refreshKey]);

  if (error && !data) {
    return <p className="error">Could not load the Trust Center: {error}</p>;
  }
  if (!data) {
    return <p className="muted">Loading…</p>;
  }

  const ev = data.evaluation;
  const s = data.session;
  const ruleCases = ev ? ev.passedAllChecks : 0;
  const span = data.lastTrace?.spans ?? [];
  const total = Math.max(1, ...span.map((x) => x.startMs + x.durationMs));
  const toolMs = span.filter((x) => x.kind === "tool").reduce((a, x) => a + x.durationMs, 0);
  const modelMs = span.filter((x) => x.kind === "model").reduce((a, x) => a + x.durationMs, 0);

  return (
    <div className="trust">
      <div className="hero">
        <div className="claim">Every number comes from the cost engine. People make every decision. Every step is traced.</div>
        <div className="pillars">
          <div className="pillar det">
            <div className="k">Deterministic</div>
            <div className="big">{ev ? `${ev.numbersUnsupported} invented numbers` : "Numbers from tools"}</div>
            <div className="small muted">{ev ? `among ${ev.numbersQuoted} numbers quoted in the last evaluation run` : "Run ./scripts/run-evals.sh to add evaluation results"}</div>
            <ul>
              {ev && <li>The cost engine matches the reference model on {ev.engineParityCases} of {ev.engineParityCases} test cases</li>}
              <li>The agent may not do its own arithmetic: every number must appear in a tool result</li>
              <li>
                Checked live on every answer: {s.numbersTraced} of {s.numbersQuoted} numbers traced today
              </li>
            </ul>
          </div>
          <div className="pillar rai">
            <div className="k">Responsible AI</div>
            <div className="big">{ev ? `${ruleCases} / ${ev.cases} cases held every rule` : "People decide"}</div>
            <div className="small muted">scope, role, region, guardrail and decision rules in the last evaluation run</div>
            <ul>
              <li>Data scope comes from your sign-in, never from the model</li>
              <li>The model can ask; only a person's click logs a decision</li>
              <li>Guardrail re-checked at approve and execute · content filter {data.controls.contentFilter}</li>
            </ul>
          </div>
          <div className="pillar obs">
            <div className="k">Observable</div>
            <div className="big">{s.answers > 0 ? `${pct(s.answersTraced, s.answers)}% of answers traced` : "Every answer traced"}</div>
            <div className="small muted">browser → gateway → agent → tools → model</div>
            <ul>
              <li>Every answer has a trace ID in Application Insights</li>
              <li>Evaluations recorded in Microsoft Foundry</li>
              <li>Managed identity on every hop: no keys anywhere</li>
            </ul>
          </div>
        </div>
      </div>

      <div className="trust-grid">
        <section className="panel">
          {ev ? (
            <>
              <h2>
                Last evaluation run{" "}
                <span className="muted small">
                  · {when(ev.createdUtc)} · {ev.cases} questions asked as {Object.entries(ev.personas).map(([p, n]) => `${p[0].toUpperCase()}${p.slice(1)} (${n})`).join(" and ")} ·{" "}
                  {ev.model}
                </span>
              </h2>
              <div className="small muted section-label">Deterministic checks (code, no model)</div>
              {["tool_selection", "numeric_accuracy", "grounded_numbers", "decision"].map((k) => ev.checks[k] && <Bar key={k} check={ev.checks[k]} />)}
              {ev.judges && (
                <>
                  <div className="small muted section-label">Microsoft Foundry judges (LLM-graded, isolated evaluation project)</div>
                  {["task_adherence", "intent_resolution", "groundedness", "tool_call_accuracy"].map(
                    (k) => ev.judges?.[k] && <Bar key={k} check={ev.judges[k]} refusals={k === "tool_call_accuracy" ? ev.expectedRefusals : 0} />,
                  )}
                  {ev.expectedRefusals > 0 && (
                    <div className="small muted">
                      ⓘ {ev.expectedRefusals} are refusals by design: requests outside a person's region or role, a guardrail breach, a declined
                      confirmation. The agent behaved correctly; the judge marks a refused call down.
                    </div>
                  )}
                </>
              )}
              <div className="caught">
                <strong>What the evaluation caught, and we fixed:</strong> "Approve A1" without a region made the agent approve all three regions, and
                once it subtracted two weeks' costs itself instead of asking the engine. Both are now rules, re-tested on every run.
              </div>
              <div className="btns">
                {data.links.foundryEvaluations && (
                  <a className="btn primary" href={data.links.foundryEvaluations} target="_blank" rel="noreferrer">
                    Open in Microsoft Foundry ↗
                  </a>
                )}
                {data.links.report && (
                  <a className="btn" href={data.links.report} target="_blank" rel="noreferrer">
                    Full report ↗
                  </a>
                )}
              </div>
            </>
          ) : (
            <>
              <h2>Last evaluation run</h2>
              <p className="muted">No evaluation summary is deployed. Run ./scripts/run-evals.sh and redeploy.</p>
            </>
          )}
        </section>

        <section className="panel">
          <h2>
            Live in this session{" "}
            <span className="live">
              <span className="dot" />
              live
            </span>
            <span className="muted small">· since {when(s.since)}</span>
          </h2>
          <div className="tkpis">
            <div className="tkpi">
              <b>{s.answers}</b>
              <span>agent answers</span>
            </div>
            <div className="tkpi">
              <b className={s.numbersToReview === 0 ? "good" : "warn"}>
                {s.numbersTraced} / {s.numbersQuoted}
              </b>
              <span>numbers traced to a tool</span>
            </div>
            <div className="tkpi">
              <b className={s.numbersToReview === 0 ? "" : "warn"}>{s.numbersToReview}</b>
              <span>numbers to review</span>
            </div>
            <div className="tkpi">
              <b className="amber">{s.refusedByPolicy}</b>
              <span>requests refused by policy</span>
            </div>
            <div className="tkpi">
              <b>{s.decisionsByPeople}</b>
              <span>decisions made by a person</span>
            </div>
            <div className="tkpi">
              <b>{seconds(s.medianMs)}</b>
              <span>median answer time</span>
            </div>
          </div>
          <h3>
            How the last answer was produced{" "}
            {data.lastTrace && <span className="muted small">· trace {data.lastTrace.traceId.slice(0, 8)}…</span>}
          </h3>
          {span.length === 0 ? (
            <p className="muted small">Ask the agent a question on the What-If Studio tab; its trace appears here.</p>
          ) : (
            <>
              <div className="muted small">“{data.lastTrace?.question}”</div>
              <div className="wf">
                {span.map((x, i) => (
                  <div className="wf-row" key={i}>
                    <span>{x.label}</span>
                    <div className="lane">
                      <div
                        className={`seg ${x.kind}${x.error ? " err" : ""}`}
                        style={{ left: `${(x.startMs / total) * 100}%`, width: `${Math.max(0.6, (x.durationMs / total) * 100)}%` }}
                      />
                    </div>
                    <span className={x.kind === "tool" ? "good" : ""}>{x.durationMs < 1000 ? `${Math.round(x.durationMs)} ms` : seconds(x.durationMs)}</span>
                  </div>
                ))}
              </div>
              {toolMs > 0 && (
                <div className="muted small">
                  The cost engine (deterministic code) ran for {Math.max(1, Math.round(toolMs))} ms; the model spent {seconds(modelMs)} choosing the tool and
                  writing the answer.
                </div>
              )}
            </>
          )}
          {data.links.appInsightsSearch && (
            <div className="btns">
              <a className="btn" href={data.links.appInsightsSearch} target="_blank" rel="noreferrer">
                Open Application Insights ↗
              </a>
              {data.lastTrace && (
                <button className="btn" onClick={() => void navigator.clipboard?.writeText(data.lastTrace!.traceId)}>
                  Copy trace ID
                </button>
              )}
            </div>
          )}
        </section>
      </div>

      <section className="panel">
        <h2>
          Every answer in this session <span className="muted small">· {caller.displayName}'s answers only · newest first</span>
        </h2>
        {data.turns.length === 0 ? (
          <p className="muted">No answers yet. Ask the agent a question on the What-If Studio tab.</p>
        ) : (
          <table className="table trust-table">
            <thead>
              <tr>
                <th>Time</th>
                <th>Question</th>
                <th>Tools the agent used</th>
                <th>Numbers</th>
                <th>Policy</th>
                <th>Time</th>
                <th>Trace</th>
              </tr>
            </thead>
            <tbody>
              {data.turns.map((t) => {
                const policy = policyNote(t.tools);
                return (
                  <tr key={t.at + t.question}>
                    <td className="nowrap">{when(t.at)}</td>
                    <td>{t.question}</td>
                    <td>
                      {t.errored && <span className="rpill review">Model error</span>}
                      {t.tools.map((c, i) => (
                        <span key={i} className={`tchip${isRefusal(c.outcome) ? " deny" : ""}`}>
                          {c.name}
                          {c.outcome !== "ok" ? ` · ${c.outcome}` : ""}
                        </span>
                      ))}
                      {!t.errored && t.tools.length === 0 && <span className="muted small">none</span>}
                    </td>
                    <td>
                      {t.quoted === 0 ? (
                        <span className="muted">–</span>
                      ) : (
                        <span className={`rpill ${t.unsupported.length === 0 ? "ok" : "review"}`}>
                          {t.traced} / {t.quoted} from tools
                        </span>
                      )}
                    </td>
                    <td>{policy.tone !== "none" && <span className={`rpill ${policy.tone === "refused" ? "expected" : "decision"}`}>{policy.text}</span>}</td>
                    <td className="nowrap">{seconds(t.durationMs)}</td>
                    <td className="mono">{t.traceId ? `${t.traceId.slice(0, 8)}…` : "–"}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </section>

      <section className="panel">
        <h2>
          Controls in force <span className="muted small">· configuration, not promises</span>
        </h2>
        <div className="ctrl">
          <div>Numbers come only from tools; every answer is checked before you see its receipt</div>
          <div>Data scope comes from your sign-in token, never from the model</div>
          <div>The model can request a decision; only a person's click records it</div>
          <div>Express 3-hour guardrail re-checked at approve and execute</div>
          <div>Azure AI content filter ({data.controls.contentFilter}) on {data.controls.model}</div>
          <div>{data.controls.modelViaGateway ? "Model calls go through the APIM AI gateway with token limits" : "Model calls use Entra tokens, not keys"}</div>
          <div>No keys or passwords: managed identity on every hop</div>
          <div>Illustrative data only; no Walmart data</div>
        </div>
      </section>
    </div>
  );
}
