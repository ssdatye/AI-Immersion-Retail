import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { api, loadPersona, savePersona } from "./api";
import { EntraAuth, loadAuthConfig, personaAuth, type ApiAuth, type AuthConfig } from "./auth";
import { focusAction, leversAt, planIn, waitingFor, waitingLabel, type WaitingItem } from "./pending";
import { ActionsPanel } from "./components/ActionsPanel";
import { ChatPanel } from "./components/ChatPanel";
import { TrustCenter } from "./components/TrustCenter";
import { FuelPanel } from "./components/FuelPanel";
import { LeversPanel } from "./components/LeversPanel";
import { Outcomes } from "./components/Outcomes";
import { PackageForm } from "./components/PackageForm";
import { OutcomesBar, type DrawerView } from "./components/OutcomesBar";
import { changesQuestion, modelChanges, settingsSummary, type ModelSettings } from "./modelSync";
import { NO_LEVERS, type Dashboard, type DecisionRecord, type Levers, type Persona, type Playbook, type ScenarioResult, type ExecutionResults } from "./types";

/** Chooses the sign-in mode the API reports: Entra (MSAL) or local dev personas. */
export function App() {
  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [entra, setEntra] = useState<EntraAuth | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [persona, setPersona] = useState<Persona>(loadPersona);

  useEffect(() => {
    loadAuthConfig()
      .then(async (c) => {
        if (c.mode === "entra") {
          setEntra(await EntraAuth.create(c));
        }
        setConfig(c);
      })
      .catch((e: Error) => setError(e.message));
  }, []);

  const devAuth = useMemo(() => personaAuth(persona), [persona]);

  if (error) {
    return <Shell>{<p className="error">{error}</p>}</Shell>;
  }
  if (!config) {
    return <Shell>{<p className="muted">Loading…</p>}</Shell>;
  }
  if (config.mode === "none") {
    return <Shell>{<p className="error">Sign-in is not configured for this environment.</p>}</Shell>;
  }

  if (config.mode === "entra" && entra) {
    const account = entra.account;
    if (!account) {
      return (
        <Shell>
          <div className="panel signin">
            <h2>Sign in</h2>
            <p>Sign in with your demo account. Finance sees the whole network; regional ops sees its own region.</p>
            <button className="primary" onClick={() => void entra.signIn()}>
              Sign in with Microsoft
            </button>
          </div>
        </Shell>
      );
    }
    return (
      <Studio
        key={account.homeAccountId}
        auth={entra}
        controls={
          <div className="persona">
            <span>
              Signed in as <strong>{account.name ?? account.username}</strong>
            </span>
            <button className="link" onClick={() => void entra.signOut()}>
              Sign out
            </button>
          </div>
        }
      />
    );
  }

  return (
    <Studio
      key={persona}
      auth={devAuth}
      controls={
        <div className="persona">
          <label>
            Demo persona{" "}
            <select
              value={persona}
              onChange={(e) => {
                savePersona(e.target.value as Persona);
                setPersona(e.target.value as Persona);
              }}
            >
              <option value="dana">Dana · Director, Fulfillment Finance</option>
              <option value="luis">Luis · Southeast Regional Delivery Ops</option>
            </select>
          </label>
          <span className="muted small">Local dev persona, no sign-in</span>
        </div>
      }
    />
  );
}

function Shell({ children, controls }: { children: ReactNode; controls?: ReactNode }) {
  return (
    <div className="app">
      <header>
        <div>
          <h1>Fuel & Cost-to-Serve Intelligence · What-If Studio</h1>
          <span className="badge">Illustrative data · not real Walmart data</span>
        </div>
        {controls}
      </header>
      {children}
    </div>
  );
}

function Studio({ auth, controls }: { auth: ApiAuth; controls: ReactNode }) {
  const [dashboard, setDashboard] = useState<Dashboard | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [dieselShift, setDieselShift] = useState(0.5);
  const [duration, setDuration] = useState(13);
  const [levers, setLevers] = useState<Levers>(NO_LEVERS);
  const [region, setRegion] = useState("");
  const [scenario, setScenario] = useState<ScenarioResult | null>(null);
  const [scenarioError, setScenarioError] = useState<string | null>(null);
  const [scenarioLoading, setScenarioLoading] = useState(false);
  const [playbook, setPlaybook] = useState<Playbook | null>(null);
  const [results, setResults] = useState<ExecutionResults | null>(null);
  // Approved (or executed) actions are always applied to the outcomes unless the user turns the plan off to compare.
  const [includePlan, setIncludePlan] = useState(true);
  const [decisions, setDecisions] = useState<DecisionRecord[]>([]);
  // Decisions apply to one region at a time: regional ops to their own, finance to the region chosen in Actions and decisions.
  const [decisionRegion, setDecisionRegion] = useState("");
  // The studio stays mounted while the Trust Center shows, so the chat and sliders keep their state.
  const [tab, setTab] = useState<"studio" | "trust">("studio");
  const [evidenceTick, setEvidenceTick] = useState(0);
  // The conversation leads; the model and decisions open in a side panel only when wanted.
  const [drawer, setDrawer] = useState<DrawerView | null>(null);
  // Model settings last shared with the agent, to show what changed since.
  const [synced, setSynced] = useState<ModelSettings>({ dieselShift: 0.5, duration: 13, levers: NO_LEVERS, region: "", includePlan: true });
  const [askRequest, setAskRequest] = useState<{ text: string; nonce: number } | null>(null);
  const barRef = useRef<HTMLElement>(null);
  const drawerRef = useRef<HTMLElement>(null);

  useEffect(() => {
    api<Dashboard>(auth, "/api/dashboard")
      .then((d) => {
        setDashboard(d);
        setDecisionRegion((current) => current || d.regions[0] || "");
      })
      .catch((e: Error) => setLoadError(e.message));
  }, [auth]);

  const refreshDecisions = useCallback(() => {
    if (!decisionRegion) {
      return;
    }
    api<Playbook>(auth, `/api/playbook?region=${encodeURIComponent(decisionRegion)}`).then(setPlaybook, () => setPlaybook(null));
    api<DecisionRecord[]>(auth, "/api/decisions").then(setDecisions, () => setDecisions([]));
    api<ExecutionResults>(auth, "/api/results").then(setResults, () => setResults(null));
  }, [auth, decisionRegion]);

  useEffect(refreshDecisions, [refreshDecisions]);

  // The plan is each region's approved and executed actions; each applies only in its own region.
  const modeledRegions = useMemo(() => (region ? [region] : (dashboard?.regions ?? [])), [region, dashboard]);
  const singleRegion = modeledRegions.length === 1 ? modeledRegions[0] : null;
  // Playbook actions take their levers from the dashboard; custom packages carry their own.
  const defaults = useMemo(
    () => ({
      ...Object.fromEntries((playbook?.actions ?? []).flatMap((a) => (a.isPackage && a.levers ? [[a.actionId, a.levers]] : []))),
      ...Object.fromEntries((dashboard?.actions ?? []).map((a) => [a.id, a.levers])),
    }),
    [dashboard, playbook],
  );
  const plan = useMemo(() => planIn(playbook?.actions ?? [], modeledRegions, defaults), [playbook, modeledRegions, defaults]);
  const planKey = plan.map((p) => `${p.region}:${p.actionId}:${p.settingText}`).join(",");
  const waiting = useMemo(() => (dashboard && playbook ? waitingFor(dashboard.caller, playbook.actions) : []), [dashboard, playbook]);
  const applyPlan = includePlan && plan.length > 0;
  const showWaiting = (w: WaitingItem) => {
    setTab("studio");
    setDrawer("decisions");
    setDecisionRegion(w.region);
    setTimeout(() => focusAction(w.actionId), 300);
  };

  // Recompute the scenario on the server (same engine and scope rules as the agent's tools):
  // approved actions plus the what-if levers on top (the server takes the larger value of each lever).
  useEffect(() => {
    if (!dashboard) {
      return;
    }
    const controller = new AbortController();
    const timer = setTimeout(() => {
      setScenarioLoading(true);
      api<ScenarioResult>(auth, "/api/scenario", {
        method: "POST",
        body: { dieselShift, durationWeeks: duration, levers, actionIds: null, applyPlan, region: region || null },
        signal: controller.signal,
      })
        .then((r) => {
          setScenario(r);
          setScenarioError(null);
        })
        .catch((e: Error) => {
          if (e.name !== "AbortError") {
            setScenarioError(e.message);
          }
        })
        .finally(() => setScenarioLoading(false));
    }, 200);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [auth, dashboard, dieselShift, duration, levers, region, applyPlan, planKey]);

  const studioContext = useMemo(() => {
    const active = Object.entries(levers)
      .filter(([, v]) => v !== 0 && v !== false)
      .map(([k, v]) => `${k}=${v}`);
    const planText = applyPlan ? plan.map((p) => `${p.actionId}${p.settingText ? ` at ${p.settingText}` : ""} in ${p.region}`).join(", ") : "none";
    return `diesel change ${dieselShift >= 0 ? "+" : ""}${dieselShift.toFixed(2)} $/gal for ${duration} weeks; approved plan applied (each action only in its region): ${planText}; decisions panel region ${decisionRegion}; extra what-if levers ${active.length ? active.join(", ") : "none"}; scope ${
      region || (dashboard?.caller.role === "RegionalOps" ? dashboard.caller.scope : "network")
    }`;
  }, [dieselShift, duration, levers, region, dashboard, applyPlan, plan, decisionRegion]);

  // Lever floors and chips use one region's decided settings; with several regions the sliders show what-ifs only.
  const regionLevers = useMemo(() => {
    const entries = (playbook?.actions ?? []).map((a) => {
      const r = singleRegion ? a.byRegion?.find((x) => x.region === singleRegion) : undefined;
      return [a.actionId, { levers: leversAt(defaults[a.actionId], r?.setting), setting: r?.setting ? `${Math.round(r.setting.value * 1000) / 10}%` : null }];
    });
    return Object.fromEntries(entries) as Record<string, { levers: Levers; setting: string | null }>;
  }, [playbook, singleRegion, defaults]);
  const lockedActions = applyPlan && singleRegion ? [...new Set(plan.map((p) => p.actionId))] : [];

  const current: ModelSettings = { dieselShift, duration, levers, region, includePlan };
  const currentRef = useRef(current);
  currentRef.current = current;
  const changes = modelChanges(synced, current);
  const askAboutChanges = () => {
    setTab("studio");
    setAskRequest({ text: changesQuestion(changes), nonce: Date.now() });
  };
  const undoChanges = () => {
    setDieselShift(synced.dieselShift);
    setDuration(synced.duration);
    setLevers(synced.levers);
    setRegion(synced.region);
    setIncludePlan(synced.includePlan);
  };

  // Keeps the side panel just below the pinned outcomes bar, and the chat sized to the space under it.
  useEffect(() => {
    const measure = () => {
      const bar = barRef.current;
      if (!bar) {
        return;
      }
      const pinned = getComputedStyle(bar).position === "sticky";
      const rect = bar.getBoundingClientRect();
      const root = document.documentElement.style;
      root.setProperty("--bar-h", `${pinned ? bar.offsetHeight : 0}px`);
      root.setProperty("--drawer-top", `${Math.max(0, Math.ceil(rect.bottom))}px`);
      // The chat fills the rest of the first screen, so the question box is visible without scrolling.
      root.setProperty("--chat-h", `${Math.max(420, window.innerHeight - (rect.bottom + window.scrollY) - 30)}px`);
    };
    measure();
    const observer = new ResizeObserver(measure);
    if (barRef.current) {
      observer.observe(barRef.current);
    }
    observer.observe(document.body);
    window.addEventListener("scroll", measure, { passive: true });
    window.addEventListener("resize", measure);
    return () => {
      observer.disconnect();
      window.removeEventListener("scroll", measure);
      window.removeEventListener("resize", measure);
    };
  }, [dashboard, tab]);

  const closeDrawer = () => {
    const was = drawer;
    setDrawer(null);
    setTimeout(() => barRef.current?.querySelector<HTMLElement>(`.obar-buttons button:nth-child(${was === "decisions" ? 2 : 1})`)?.focus(), 0);
  };

  const openDrawer = (view: DrawerView | null) => {
    setDrawer(view);
    if (view) {
      setTimeout(() => drawerRef.current?.querySelector<HTMLElement>("h2")?.focus(), 50);
    }
  };
  const planNote =
    applyPlan && !singleRegion
      ? `Approved actions apply only in their own region (${plan.map((p) => `${p.actionId} in ${p.region}`).join(", ")}). ` +
        "The outcomes include them; the sliders show what-ifs for every region. Pick a region under Model to see its plan on the sliders."
      : null;

  return (
    <Shell controls={controls}>
      {loadError && <p className="error">Could not load the studio: {loadError}</p>}
      {!dashboard && !loadError && <p className="muted">Loading…</p>}

      {dashboard && (
        <>
          <div className="who-bar">
            <strong>{dashboard.caller.displayName}</strong> · {dashboard.caller.title} · role {dashboard.caller.role} · data scope{" "}
            <strong>{dashboard.caller.scope}</strong> · this week {dashboard.period.currentWeek} · seed {dashboard.seedVersion}
            {waiting.length > 0 && (
              <button className="waiting-badge" onClick={() => showWaiting(waiting[0])} title="Show the first one">
                🔔 {waitingLabel(dashboard.caller, waiting.length)}: {waiting.map((a) => `${a.actionId} (${a.region})`).join(", ")}
              </button>
            )}
          </div>
          <div
            className="tabs"
            role="tablist"
            aria-label="Studio views"
            onKeyDown={(e) => {
              if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
                const next = tab === "studio" ? "trust" : "studio";
                setTab(next);
                document.getElementById(`tab-${next}`)?.focus();
              }
            }}
          >
            <button
              role="tab"
              id="tab-studio"
              aria-selected={tab === "studio"}
              aria-controls="panel-studio"
              tabIndex={tab === "studio" ? 0 : -1}
              className={tab === "studio" ? "active" : ""}
              onClick={() => setTab("studio")}
            >
              What-If Studio
            </button>
            <button
              role="tab"
              id="tab-trust"
              aria-selected={tab === "trust"}
              aria-controls="panel-trust"
              tabIndex={tab === "trust" ? 0 : -1}
              className={tab === "trust" ? "active" : ""}
              onClick={() => setTab("trust")}
            >
              🛡 Trust Center
            </button>
          </div>
          <div hidden={tab !== "trust"} role="tabpanel" id="panel-trust" aria-labelledby="tab-trust">
            <TrustCenter auth={auth} caller={dashboard.caller} visible={tab === "trust"} refreshKey={evidenceTick} onShowStudio={() => setTab("studio")} />
          </div>
          <div className="studio" hidden={tab !== "studio"} role="tabpanel" id="panel-studio" aria-labelledby="tab-studio">
            <OutcomesBar
              ref={barRef}
              result={scenario}
              loading={scenarioLoading}
              error={scenarioError}
              summary={settingsSummary(current, plan.length)}
              changes={changes}
              onAskChanges={askAboutChanges}
              drawer={drawer}
              onDrawer={openDrawer}
              waitingCount={waiting.length}
            />
            <div className={`studio-body ${drawer ? "drawer-open" : ""}`}>
              <main className="studio-main">
                <ChatPanel
                  auth={auth}
                  displayName={dashboard.caller.displayName}
                  suggestions={dashboard.suggestions}
                  studioContext={studioContext}
                  playbook={playbook}
                  waiting={waiting}
                  decisionRegion={decisionRegion}
                  onDecisionLogged={refreshDecisions}
                  onEvidence={() => setEvidenceTick((n) => n + 1)}
                  askRequest={askRequest}
                  onShared={() => setSynced(currentRef.current)}
                  onOpenTrust={() => setTab("trust")}
                />
              </main>
              <aside
                id="studio-drawer"
                className="drawer"
                hidden={!drawer}
                ref={drawerRef}
                aria-labelledby="drawer-h"
                onKeyDown={(e) => {
                  if (e.key === "Escape") {
                    closeDrawer();
                  }
                }}
              >
                <div className="drawer-head">
                  <div>
                    <h2 id="drawer-h" tabIndex={-1}>
                      {drawer === "decisions" ? "Actions and decisions" : "Fine-tune the model"}
                    </h2>
                    <span className="muted small">{drawer === "decisions" ? "People decide, region by region" : "Cost engine · instant · no AI"}</span>
                  </div>
                  <button type="button" onClick={closeDrawer}>
                    Close ▸
                  </button>
                </div>
                <div className="drawer-content">
                  {/* Both views stay mounted so drafts and positions survive switching. */}
                  <div hidden={drawer !== "model"}>
                    <FuelPanel
                      dashboard={dashboard}
                      dieselShift={dieselShift}
                      duration={duration}
                      onShift={setDieselShift}
                      onDuration={setDuration}
                      onPreset={(sh, d) => {
                        setDieselShift(sh);
                        setDuration(d);
                      }}
                    />
                    <LeversPanel
                      dashboard={dashboard}
                      levers={levers}
                      onChange={setLevers}
                      region={region}
                      onRegion={setRegion}
                      lockedActions={lockedActions}
                      onLockedClick={(id) => {
                        if (singleRegion) {
                          setDecisionRegion(singleRegion);
                        }
                        setDrawer("decisions");
                        setTimeout(() => focusAction(id), 300);
                      }}
                      actionLevers={regionLevers}
                      planNote={planNote}
                    />
                    <Outcomes
                      result={scenario}
                      error={scenarioError}
                      loading={scenarioLoading}
                      costRows={dashboard.costPerOrder}
                      period={dashboard.period}
                      planActions={plan.map((p) => ({ id: p.actionId, region: p.region, setting: p.settingText, executed: p.executed }))}
                      multiRegion={!singleRegion}
                      includePlan={includePlan}
                      onIncludePlan={setIncludePlan}
                      showKpis={false}
                    />
                  </div>
                  <div hidden={drawer !== "decisions"}>
                    <ActionsPanel
                      auth={auth}
                      caller={dashboard.caller}
                      playbook={playbook}
                      decisions={decisions}
                      results={results}
                      decisionRegion={decisionRegion}
                      regions={dashboard.regions}
                      onRegion={setDecisionRegion}
                      waiting={waiting}
                      onChanged={refreshDecisions}
                    />
                  </div>
                </div>
                {drawer === "model" && (
                  <div className={`drawer-foot ${changes.length ? "pending" : ""}`}>
                    <div aria-live="polite">
                      {changes.length === 0 ? (
                        <p className="small muted">Changes here are instant and stay with you until you ask the agent about them.</p>
                      ) : (
                        <>
                          <strong>
                            You changed {changes.length} {changes.length === 1 ? "setting" : "settings"} · not shared with the agent yet
                          </strong>
                          <ul>
                            {changes.map((c) => (
                              <li key={c.key}>
                                {c.label}: {c.from} → <strong>{c.to}</strong>
                              </li>
                            ))}
                          </ul>
                          <div className="drawer-foot-buttons">
                            <button type="button" className="primary" onClick={askAboutChanges}>
                              Ask the agent about {changes.length === 1 ? "this change" : "these changes"}
                            </button>
                            <button type="button" onClick={undoChanges}>
                              Undo
                            </button>
                          </div>
                        </>
                      )}
                    </div>
                    {dashboard.caller.role === "RegionalOps" && (
                      <PackageForm
                        auth={auth}
                        region={dashboard.caller.scope}
                        levers={levers}
                        onProposed={refreshDecisions}
                        onOpen={(id) => {
                          setDecisionRegion(dashboard.caller.scope);
                          setDrawer("decisions");
                          setTimeout(() => focusAction(id), 300);
                        }}
                      />
                    )}
                  </div>
                )}
              </aside>
            </div>
          </div>
        </>
      )}
    </Shell>
  );
}
