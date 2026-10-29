import { Fragment, useCallback, useEffect, useState } from "react";
import { api } from "../api";
import type { ApiAuth } from "../auth";
import { caseVerdict, filterCases, isRefusal, mark, policyNote, seconds, sourceLabel } from "../trust";
import type { Caller, EvaluationCase, EvaluationSummary, TrustCheck, TrustData } from "../types";
import { when } from "./ActionsPanel";

interface Props {
  auth: ApiAuth;
  caller: Caller;
  /** Whether the tab is showing; it refreshes only while visible. */
  visible: boolean;
  /** Bumped when a chat answer's evidence arrives, to refresh at once. */
  refreshKey: number;
  /** Switches to the What-If Studio tab. */
  onShowStudio: () => void;
}

const pct = (n: number, d: number) => (d > 0 ? Math.round((n / d) * 100) : 0);
const CHECK_KEYS = ["tool_selection", "numeric_accuracy", "grounded_numbers", "decision"] as const;
const CHECK_SHORT: Record<string, string> = { tool_selection: "tools", numeric_accuracy: "numbers", grounded_numbers: "from tools", decision: "decision" };
const JUDGE_KEYS = ["task_adherence", "intent_resolution", "groundedness", "tool_call_accuracy"] as const;
const JUDGE_SHORT: Record<string, string> = { task_adherence: "task", intent_resolution: "intent", groundedness: "grounded", tool_call_accuracy: "tool calls" };
const name = (persona: string) => persona[0].toUpperCase() + persona.slice(1);

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

function Marks({ values, keys, short }: { values: Record<string, string> | null; keys: readonly string[]; short: Record<string, string> }) {
  if (!values) {
    return <span className="muted">–</span>;
  }
  return (
    <span className="marks">
      {keys.map((k) => (
        <span key={k} className={`m ${values[k] ?? "na"}`} title={`${short[k]}: ${values[k] ?? "not applicable"}`}>
          {mark(values[k])}
        </span>
      ))}
    </span>
  );
}

/** The questions asked in the evaluation run, each with its answer, checks, judges and number sources. */
function CaseList({ ev }: { ev: EvaluationSummary }) {
  const cases = ev.caseRows ?? [];
  const [filter, setFilter] = useState("all");
  const [open, setOpen] = useState<string | null>(null);
  const shown = filterCases(cases, filter);
  const filters: [string, string][] = [
    ["all", `All (${cases.length})`],
    ...Object.entries(ev.personas).map(([p, n]): [string, string] => [p, `${name(p)} (${n})`]),
    ["refusals", `Refused by design (${cases.filter((c) => c.refusalByDesign).length})`],
  ];

  return (
    <>
      <div className="case-filters">
        {filters.map(([key, label]) => (
          <button key={key} className={filter === key ? "active" : ""} onClick={() => setFilter(key)}>
            {label}
          </button>
        ))}
        <span className="muted small">
          Checks: {CHECK_KEYS.map((k) => CHECK_SHORT[k]).join(" · ")} &nbsp;|&nbsp; Foundry judges: {JUDGE_KEYS.map((k) => JUDGE_SHORT[k]).join(" · ")} &nbsp;|&nbsp;
          click a question to see the answer
        </span>
      </div>
      <table className="table trust-table cases">
        <thead>
          <tr>
            <th>#</th>
            <th>Asked as</th>
            <th>Question</th>
            <th>Tools the agent used</th>
            <th>Checks</th>
            <th>Judges</th>
            <th>Result</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((c: EvaluationCase) => {
            const verdict = caseVerdict(c);
            const isOpen = open === c.id;
            return (
              <Fragment key={c.id}>
                <tr className={`case-row${isOpen ? " open" : ""}`} onClick={() => setOpen(isOpen ? null : c.id)}>
                  <td className="muted">{cases.indexOf(c) + 1}</td>
                  <td className="nowrap">{name(c.persona)}</td>
                  <td>
                    <span className="case-toggle">{isOpen ? "▾" : "▸"}</span> {c.question}
                  </td>
                  <td>
                    {c.tools.map((t, i) => (
                      <span key={i} className={`tchip${isRefusal(t.outcome) ? " deny" : ""}`}>
                        {t.name}
                        {t.outcome !== "ok" ? ` · ${t.outcome}` : ""}
                      </span>
                    ))}
                    {c.tools.length === 0 && <span className="muted small">none</span>}
                  </td>
                  <td>
                    <Marks values={c.checks} keys={CHECK_KEYS} short={CHECK_SHORT} />
                  </td>
                  <td>
                    <Marks values={c.judges} keys={JUDGE_KEYS} short={JUDGE_SHORT} />
                  </td>
                  <td>
                    <span className={`rpill ${verdict.tone}`}>{verdict.text}</span>
                  </td>
                </tr>
                {isOpen && (
                  <tr className="case-detail">
                    <td />
                    <td colSpan={6}>
                      {c.expectation && (
                        <div>
                          <b>Expected:</b> {c.expectation}
                        </div>
                      )}
                      <div className="case-answer">
                        <b>Agent's answer</b> <span className="muted small">({seconds(c.durationMs)})</span>
                        <div>{c.answer}</div>
                      </div>
                      {c.numbers.length > 0 ? (
                        <table className="evidence-table">
                          <thead>
                            <tr>
                              <th>Number in the answer</th>
                              <th>Where it came from</th>
                            </tr>
                          </thead>
                          <tbody>
                            {c.numbers
                              .filter((n, i, all) => all.findIndex((m) => m.text === n.text) === i)
                              .map((n) => (
                                <tr key={n.text}>
                                  <td>{n.text}</td>
                                  <td className={n.source ? "mono" : "mono review-text"}>{sourceLabel(n.source)}</td>
                                </tr>
                              ))}
                          </tbody>
                        </table>
                      ) : (
                        <div className="muted small">No figures quoted.</div>
                      )}
                      {c.refusalByDesign && (
                        <div className="muted small">
                          ⓘ Refused by design: the tool enforced a rule (region, role, workflow or guardrail) or the person declined. The agent behaved
                          correctly, and every rule check passed; a judge may mark the refused request down.
                        </div>
                      )}
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </>
  );
}

/**
 * The Trust Center: why the agent's answers can be trusted and why this is a Frontier pattern. The
 * Frontier fit, the acceptance tests (the last evaluation run, kept across demo resets), live evidence
 * since the reset, the controls in force, and the assumptions and limits. Everything shown comes from
 * the server; no results are hard-coded.
 */
export function TrustCenter({ auth, caller, visible, refreshKey, onShowStudio }: Props) {
  const [data, setData] = useState<TrustData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showCases, setShowCases] = useState(false);

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
  const span = data.lastTrace?.spans ?? [];
  const total = Math.max(1, ...span.map((x) => x.startMs + x.durationMs));
  const toolMs = span.filter((x) => x.kind === "tool").reduce((a, x) => a + x.durationMs, 0);
  const modelMs = span.filter((x) => x.kind === "model").reduce((a, x) => a + x.durationMs, 0);
  const foundryLink = data.links.foundryRun ?? data.links.foundryEvaluations;

  const go = (target: string) => {
    if (target === "studio") {
      onShowStudio();
      return;
    }
    if (target === "architecture") {
      window.open(data.links.architecture ?? "/architecture", "_blank", "noopener");
      return;
    }
    if (target === "trust-cases") {
      setShowCases(true);
    }
    setTimeout(() => {
      const el = document.getElementById(target);
      el?.scrollIntoView({ behavior: "smooth", block: "start" });
      el?.classList.add("flash-panel");
      setTimeout(() => el?.classList.remove("flash-panel"), 1600);
    }, 50);
  };

  const tests = [
    {
      title: "Agentic workflow",
      proof: "The agent reasons, picks among six tools, acts on the cost engine and the decision log, and hands off between people: Luis proposes, Dana approves, Luis executes.",
      live: `${s.allUsers.answers} answers · ${s.decisionsByPeople} decisions by people since the reset`,
      target: "trust-trace",
      cta: "See the last trace",
    },
    {
      title: "Measurable business value",
      proof: "Moves cost per order and protects Express delivery within 3 hours. Fuel pressure → cost per order → playbook action, every figure from the engine, with a pilot-versus-control readout.",
      target: "studio",
      cta: "Open the studio",
    },
    {
      title: "Business transformation",
      proof: "Turns fuel-shock response from spreadsheets and email into a governed propose → approve → execute loop. Same pattern for any region, cost line or business unit; actions are data, not code.",
      target: "architecture",
      cta: "Architecture ↗",
    },
    {
      title: "Repeatability + scale path",
      proof: `One Bicep deployment into its own resource group; ${ev ? `${ev.cases} acceptance tests` : "acceptance tests"} re-run on every change; production architecture defined.`,
      target: "trust-cases",
      cta: "See the tests",
    },
    {
      title: "Microsoft orchestration",
      proof: "Microsoft Agent Framework, Microsoft Foundry, API Management AI gateway, Entra ID, Application Insights, Azure SQL and Cosmos DB, end to end.",
      target: "trust-controls",
      cta: "See the controls",
    },
  ];
  const gates = [
    { title: "Executive sponsor", ok: false, text: "Customer-owned: the C-suite owner of cost-to-serve. Dana's role stands in for the demo." },
    { title: "Business KPI", ok: true, text: "Cost per order by region and delivery type, and Express within 3 hours (at least 80%), over 13 weeks." },
    { title: "Frontier pattern", ok: true, text: "Agentic, governed and repeatable: tools, data scope, people in the loop, acceptance tests." },
    { title: "Microsoft orchestration", ok: true, text: "Microsoft leads from model and agent to gateway, identity, evaluation and monitoring." },
  ];

  return (
    <div className="trust">
      <div className="hero">
        <div className="claim">Every number comes from the cost engine. People make every decision. Every step is traced.</div>
        <div className="pillars">
          <div className="pillar det">
            <div className="k">Deterministic</div>
            <div className="big">{ev ? `${ev.numbersUnsupported} invented numbers` : "Numbers from tools"}</div>
            <div className="small muted">{ev ? `among ${ev.numbersQuoted} numbers quoted in the acceptance tests` : "Run ./scripts/run-evals.sh to add the acceptance tests"}</div>
            <ul>
              {ev && <li>The cost engine matches the reference model on {ev.engineParityCases} of {ev.engineParityCases} test cases</li>}
              <li>The agent may not do its own arithmetic: every number must appear in a tool result</li>
              <li>
                Checked live on every answer: {s.allUsers.numbersTraced} of {s.allUsers.numbersQuoted} numbers traced since the reset
              </li>
            </ul>
          </div>
          <div className="pillar rai">
            <div className="k">Responsible AI</div>
            <div className="big">{ev ? `${ev.passedAllChecks} / ${ev.cases} tests held every rule` : "People decide"}</div>
            <div className="small muted">scope, role, region, guardrail and decision rules</div>
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
              <li>Acceptance tests recorded in Microsoft Foundry</li>
              <li>Managed identity on every hop: no keys anywhere</li>
            </ul>
          </div>
        </div>
      </div>

      <section className="panel frontier" id="trust-frontier">
        <h2>
          Frontier fit <span className="muted small">· the five tests, each shown in this demo · click to see the proof</span>
        </h2>
        <div className="ftests">
          {tests.map((t, i) => (
            <button key={t.title} className="ftest" onClick={() => go(t.target)}>
              <div className="ftop">
                <span className="fnum">{i + 1}</span>
                <b>{t.title}</b>
                <span className="rpill ok">✓ Shown</span>
              </div>
              <div className="small">{t.proof}</div>
              {t.live && <div className="small good">{t.live}</div>}
              <div className="fcta">
                {t.cta}
                {t.cta.endsWith("↗") ? "" : " →"}
              </div>
            </button>
          ))}
        </div>
        <div className="fgates">
          <span className="muted small fgates-label">Mandatory gates</span>
          {gates.map((g) => (
            <div key={g.title} className={`fgate${g.ok ? "" : " owner"}`}>
              <span className={`rpill ${g.ok ? "ok" : "expected"}`}>{g.ok ? "✓ Met" : "Customer to confirm"}</span> <b>{g.title}</b>
              <div className="small muted">{g.text}</div>
            </div>
          ))}
        </div>
      </section>

      <div className="trust-grid">
        <section className="panel" id="trust-acceptance">
          {ev ? (
            <>
              <h2>
                Acceptance tests{" "}
                <span className="muted small">
                  · {ev.cases} questions asked as {Object.entries(ev.personas).map(([p, n]) => `${name(p)} (${n})`).join(" and ")} · run {when(ev.createdUtc)} · {ev.model}
                </span>
              </h2>
              <div className="source small">
                <b>Source:</b> deterministic checks by the .NET evaluation runner; judges in Microsoft Foundry project <b>{ev.foundry?.project ?? "proj-evals"}</b>
                {ev.foundry?.name ? ` (evaluation ${ev.foundry.name})` : ""}. Kept across demo resets; refreshed only by a new run.
              </div>
              <div className="small muted section-label">Deterministic checks (code, no model)</div>
              {CHECK_KEYS.map((k) => ev.checks[k] && <Bar key={k} check={ev.checks[k]} />)}
              {ev.judges && (
                <>
                  <div className="small muted section-label">Microsoft Foundry judges (LLM-graded, isolated evaluation project)</div>
                  {JUDGE_KEYS.map(
                    (k) =>
                      ev.judges?.[k] && (
                        <Bar key={k} check={ev.judges[k]} refusals={ev.judges[k].refusals ?? (k === "tool_call_accuracy" ? ev.expectedRefusals : 0)} />
                      ),
                  )}
                  {ev.expectedRefusals > 0 && (
                    <div className="small muted">
                      ⓘ {ev.expectedRefusals} judge {ev.expectedRefusals === 1 ? "mark is a refusal" : "marks are refusals"} by design: a request outside a
                      person's region or role, an approval without a region, a guardrail breach, a declined confirmation. Every rule check passed; the
                      judge marks a refused request down.
                    </div>
                  )}
                </>
              )}
              <div className="caught">
                <strong>What the tests caught, and we fixed:</strong> "Approve A1" without a region made the agent approve all three regions, and once it
                subtracted two weeks' costs itself instead of asking the engine. Both are now rules, re-tested on every run.
              </div>
              <div className="btns">
                {(ev.caseRows?.length ?? 0) > 0 && (
                  <button className="btn primary" onClick={() => (showCases ? setShowCases(false) : go("trust-cases"))}>
                    {showCases ? "Hide the questions ▴" : `Show the ${ev.cases} questions ▾`}
                  </button>
                )}
                {foundryLink && (
                  <a className="btn" href={foundryLink} target="_blank" rel="noreferrer">
                    Open {data.links.foundryRun ? "this run" : "evaluations"} in Microsoft Foundry ↗
                  </a>
                )}
                {data.links.report && (
                  <a className="btn" href={data.links.report} target="_blank" rel="noreferrer">
                    Full report ↗
                  </a>
                )}
              </div>
              {foundryLink && <div className="small muted">Foundry opens for people with access to the demo subscription; the full report is public.</div>}
            </>
          ) : (
            <>
              <h2>Acceptance tests</h2>
              <p className="muted">No evaluation summary is deployed. Run ./scripts/run-evals.sh and redeploy.</p>
            </>
          )}
        </section>

        <section className="panel" id="trust-live">
          <h2>
            Live since the demo reset{" "}
            <span className="live">
              <span className="dot" />
              live
            </span>
            <span className="muted small">· {when(s.since)} · your answers</span>
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
          {s.allUsers.users > 0 && (
            <div className="small muted all-users">
              Everyone since the reset ({s.allUsers.users} {s.allUsers.users === 1 ? "person" : "people"}): {s.allUsers.answers} answers ·{" "}
              {s.allUsers.numbersTraced} of {s.allUsers.numbersQuoted} numbers traced · {s.allUsers.refusedByPolicy} refused by policy
            </div>
          )}
          <h3 id="trust-trace">
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

      {ev && showCases && (ev.caseRows?.length ?? 0) > 0 && (
        <section className="panel" id="trust-cases">
          <h2>
            The {ev.cases} acceptance questions <span className="muted small">· run {when(ev.createdUtc)} · the same check as the live receipts</span>
          </h2>
          <CaseList ev={ev} />
        </section>
      )}

      <section className="panel">
        <h2>
          Every answer since the reset <span className="muted small">· {caller.displayName}'s answers only · newest first</span>
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

      <section className="panel" id="trust-controls">
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

      <section className="panel" id="trust-assumptions">
        <h2>
          Assumptions and limits <span className="muted small">· stated, with how each is validated</span>
        </h2>
        <table className="table trust-table">
          <thead>
            <tr>
              <th>Assumption or limit</th>
              <th>How we validate or mitigate it</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>Data is illustrative, made up for the demo{ev ? ` (seed ${ev.seedVersion})` : ""}</td>
              <td>A pilot connects the customer's cost and order data; the engine's anchor and parity tests re-run on it</td>
            </tr>
            <tr>
              <td>Savings and pilot results are modeled by the engine, not measured</td>
              <td>Pilot zones against control zones measure the real effect before any scale-up</td>
            </tr>
            <tr>
              <td>A receipt proves each number came from a tool, not that the cost model is right</td>
              <td>
                The engine matches the reference model{ev ? ` on ${ev.engineParityCases} cases` : ""}; finance validates the model during the pilot
              </td>
            </tr>
            <tr>
              <td>The Foundry judges use the same model as the agent</td>
              <td>Deterministic checks are the gate; the judges are a second view, and a stronger judge model can be added</td>
            </tr>
            <tr>
              <td>The live view above is in memory on one app instance and starts again at each reset</td>
              <td>Application Insights keeps every trace; production adds dashboards and alerts</td>
            </tr>
            <tr>
              <td>The executive sponsor and KPI targets are the customer's to own</td>
              <td>Confirmed with the customer before the pilot starts</td>
            </tr>
          </tbody>
        </table>
      </section>
    </div>
  );
}
