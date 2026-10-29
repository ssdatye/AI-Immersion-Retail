import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, loadPersona, savePersona } from "./api";
import { EntraAuth, loadAuthConfig, personaAuth, type ApiAuth, type AuthConfig } from "./auth";
import { focusAction, waitingFor, waitingLabel } from "./pending";
import { toPercent } from "./format";
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

  useEffect(() => {
    api<Dashboard>(auth, "/api/dashboard")
      .then(setDashboard)
      .catch((e: Error) => setLoadError(e.message));
  }, [auth]);

  const refreshDecisions = useCallback(() => {
    api<Playbook>(auth, `/api/playbook${region ? `?region=${encodeURIComponent(region)}` : ""}`).then(setPlaybook, () => setPlaybook(null));
    api<DecisionRecord[]>(auth, "/api/decisions").then(setDecisions, () => setDecisions([]));
    api<ExecutionResults>(auth, "/api/results").then(setResults, () => setResults(null));
  }, [auth, region]);

  useEffect(refreshDecisions, [refreshDecisions]);

  const planActions = useMemo(
    () => (playbook?.actions ?? []).filter((a) => a.status === "Approved" || a.status === "Executed"),
    [playbook],
  );
  const planIds = planActions.map((a) => a.actionId).join(",");
  // The server applies each action at its decided setting; the lever floors use the same values.
  const actionLevers = useMemo(
    () =>
      Object.fromEntries(
        (playbook?.actions ?? [])
          .filter((a) => a.levers)
          .map((a) => [a.actionId, { levers: a.levers!, setting: a.setting ? `${toPercent(a.setting.value)}%` : null }]),
      ),
    [playbook],
  );
  const waiting = useMemo(() => (dashboard && playbook ? waitingFor(dashboard.caller, playbook.actions) : []), [dashboard, playbook]);
  const appliedPlanIds = includePlan ? planIds : "";

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
        body: { dieselShift, durationWeeks: duration, levers, actionIds: appliedPlanIds || null, region: region || null },
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
  }, [auth, dashboard, dieselShift, duration, levers, region, appliedPlanIds]);

  const studioContext = useMemo(() => {
    const active = Object.entries(levers)
      .filter(([, v]) => v !== 0 && v !== false)
      .map(([k, v]) => `${k}=${v}`);
    return `diesel change ${dieselShift >= 0 ? "+" : ""}${dieselShift.toFixed(2)} $/gal for ${duration} weeks; approved actions applied: ${
      appliedPlanIds || "none"
    }; extra what-if levers ${active.length ? active.join(", ") : "none"}; scope ${
      region || (dashboard?.caller.role === "RegionalOps" ? dashboard.caller.scope : "network")
    }`;
  }, [dieselShift, duration, levers, region, dashboard, appliedPlanIds]);

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
              <button className="waiting-badge" onClick={() => focusAction(waiting[0].actionId)} title="Show the first one">
                🔔 {waitingLabel(dashboard.caller, waiting.length)}: {waiting.map((a) => a.actionId).join(", ")}
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
                lockedActions={appliedPlanIds ? appliedPlanIds.split(",") : []}
                onLockedClick={focusAction}
                actionLevers={actionLevers}
              />
            </div>
            <div className="column wide">
              <Outcomes
                result={scenario}
                error={scenarioError}
                loading={scenarioLoading}
                costRows={dashboard.costPerOrder}
                period={dashboard.period}
                planActions={planActions.map((a) => ({ id: a.actionId, executed: a.status === "Executed" }))}
                includePlan={includePlan}
                onIncludePlan={setIncludePlan}
              />
              <ActionsPanel
                auth={auth}
                caller={dashboard.caller}
                playbook={playbook}
                decisions={decisions}
                results={results}
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
                onDecisionLogged={refreshDecisions}
              />
            </div>
          </main>
        </>
      )}
    </Shell>
  );
}
