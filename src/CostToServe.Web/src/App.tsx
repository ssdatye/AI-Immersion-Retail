import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, loadPersona, savePersona } from "./api";
import { EntraAuth, loadAuthConfig, personaAuth, type ApiAuth, type AuthConfig } from "./auth";
import { focusAction, leversAt, planIn, waitingFor, waitingLabel, type WaitingItem } from "./pending";
import { ActionsPanel } from "./components/ActionsPanel";
import { ChatPanel } from "./components/ChatPanel";
import { FuelPanel } from "./components/FuelPanel";
import { LeversPanel } from "./components/LeversPanel";
import { Outcomes } from "./components/Outcomes";
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
  const defaults = useMemo(() => Object.fromEntries((dashboard?.actions ?? []).map((a) => [a.id, a.levers])), [dashboard]);
  const plan = useMemo(() => planIn(playbook?.actions ?? [], modeledRegions, defaults), [playbook, modeledRegions, defaults]);
  const planKey = plan.map((p) => `${p.region}:${p.actionId}:${p.settingText}`).join(",");
  const waiting = useMemo(() => (dashboard && playbook ? waitingFor(dashboard.caller, playbook.actions) : []), [dashboard, playbook]);
  const applyPlan = includePlan && plan.length > 0;
  const showWaiting = (w: WaitingItem) => {
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
          <main>
            <div className="column">
              <FuelPanel
                dashboard={dashboard}
                dieselShift={dieselShift}
                duration={duration}
                onShift={setDieselShift}
                onDuration={setDuration}
                onPreset={(s, d) => {
                  setDieselShift(s);
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
                  setTimeout(() => focusAction(id), 300);
                }}
                actionLevers={regionLevers}
                planNote={planNote}
              />
            </div>
            <div className="column wide">
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
              />
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
            <div className="column">
              <ChatPanel
                auth={auth}
                displayName={dashboard.caller.displayName}
                suggestions={dashboard.suggestions}
                studioContext={studioContext}
                playbook={playbook}
                waiting={waiting}
                decisionRegion={decisionRegion}
                onDecisionLogged={refreshDecisions}
              />
            </div>
          </main>
        </>
      )}
    </Shell>
  );
}
