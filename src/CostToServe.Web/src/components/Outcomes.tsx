import type { CostRow, ScenarioResult } from "../types";
import { Bars } from "./Charts";
import { pct, usd, usdCompact } from "../format";

interface Props {
  result: ScenarioResult | null;
  error: string | null;
  loading: boolean;
  costRows: CostRow[];
  period: { currentWeek: string; priorWeek: string };
  /** Approved or executed actions in the modeled regions; each applies only in its own region. */
  planActions: { id: string; region: string; setting: string | null; executed: boolean }[];
  /** True when several regions are modeled, so each plan item shows its region. */
  multiRegion: boolean;
  includePlan: boolean;
  onIncludePlan: (value: boolean) => void;
  /** False when the headline numbers are shown elsewhere (the pinned outcomes bar). */
  showKpis?: boolean;
}

export function Outcomes({ result, error, loading, costRows, period, planActions, multiRegion, includePlan, onIncludePlan, showKpis = true }: Props) {
  return (
    <section className="panel">
      <h2>
        {showKpis ? "Scenario outcomes" : "Outcome details"} {result && <span className="muted">· {result.scope}</span>}{" "}
        {loading && <span className="muted">· updating…</span>}
      </h2>
      {error && <p className="error">{error}</p>}
      {result && (
        <>
          <div className="kpis" hidden={!showKpis}>
            <div className={`kpi ${result.fuelShockQuarterCostUsd > 0 ? "bad" : "good"}`}>
              <div className="value">{usdCompact(result.fuelShockQuarterCostUsd)}</div>
              <div className="label">
                Fuel shock cost this quarter ({result.dieselChangeUsdPerGallon >= 0 ? "+" : "-"}${Math.abs(result.dieselChangeUsdPerGallon).toFixed(2)}/gal ×{" "}
                {result.durationWeeks} wk)
              </div>
            </div>
            <div className={`kpi ${result.netOperatingIncomeImpactUsd < 0 ? "bad" : "good"}`}>
              <div className="value">{usdCompact(result.netOperatingIncomeImpactUsd)}</div>
              <div className="label">Net operating income impact with {planActions.length > 0 && includePlan ? "approved plan + levers" : "levers"}</div>
            </div>
            <div className="kpi">
              <div className="value">{result.offsetOfFuelShockPct == null ? "-" : pct(result.offsetOfFuelShockPct)}</div>
              <div className="label">Share of fuel shock offset{planActions.length > 0 && includePlan ? " (approved plan + levers)" : " by levers"}</div>
            </div>
            <div className={`kpi ${result.guardrail.passed ? "good" : "bad"}`}>
              <div className="value">{pct(result.expressWithin3HoursPct, 1)}</div>
              <div className="label">
                Express within 3 hours (guardrail ≥ {result.guardrail.thresholdPct}%) {result.guardrail.passed ? "✔" : "✖"}
              </div>
            </div>
          </div>
          <div className={`plan ${planActions.length === 0 ? "empty" : includePlan ? "" : "off"}`}>
            <div className="plan-title">
              Approved plan
              {planActions.length > 0 && (
                <span className="plan-ids">
                  {planActions.map((a) => (
                    <span
                      key={`${a.id}:${a.region}`}
                      className={`status ${a.executed ? "st-executed" : "st-approved"}`}
                      title={`${a.executed ? "Executed" : "Approved"} for ${a.region}${a.setting ? ` at ${a.setting}` : ""}`}
                    >
                      {a.id}
                      {a.setting ? ` ${a.setting}` : ""}
                      {multiRegion ? ` · ${a.region}` : ""}
                      {a.executed ? " ✓" : ""}
                    </span>
                  ))}
                </span>
              )}
              {planActions.length > 0 && (
                <label className="plan-toggle">
                  <input type="checkbox" checked={includePlan} onChange={(e) => onIncludePlan(e.target.checked)} /> Apply to outcomes
                </label>
              )}
            </div>
            <div className="muted small">
              {planActions.length === 0
                ? "No approved actions yet. Approved actions are applied to these outcomes automatically; levers add what-ifs on top."
                : includePlan
                  ? multiRegion
                    ? "Included above and below, each action in its own region only. Levers add what-ifs on top, in every region."
                    : "Included in the numbers above and below. Levers add what-ifs on top of the approved plan."
                  : "Not applied: the numbers show the fuel change and levers only, for comparison."}
            </div>
          </div>
          <Bars
            title="Weekly delivery cost"
            format={(v) => usdCompact(v)}
            items={[
              { label: "Today", value: result.weeklyCostUsd.baseline, color: "#94a3b8" },
              { label: "With fuel change", value: result.weeklyCostUsd.withFuelShock, color: "#dc2626" },
              { label: planActions.length > 0 && includePlan ? "With plan + levers only" : "With levers only", value: result.weeklyCostUsd.withLevers, color: "#16a34a" },
              { label: planActions.length > 0 && includePlan ? "Fuel change + plan" : "Fuel change + levers", value: result.weeklyCostUsd.withFuelShockAndLevers, color: "#2563eb" },
            ]}
          />
          <div className="service">
            <span>Promise kept {pct(result.promiseKeptPct, 1)}</span>
            <span>Availability {pct(result.availabilityPct, 1)}</span>
          </div>
          {result.guardrail.findings.length > 0 && (
            <ul className="findings">
              {result.guardrail.findings.map((f) => (
                <li key={f.code} className={f.severity === "Blocking" ? "blocking" : ""}>
                  <span className="tag">{f.severity}</span> {f.message}
                </li>
              ))}
            </ul>
          )}
          <details>
            <summary>How this is calculated</summary>
            <p className="muted small">{result.method}</p>
          </details>
        </>
      )}

      <h3>
        Cost per order <span className="muted">· actual, week of {period.priorWeek.slice(5, 10)} vs week of {period.currentWeek.slice(5, 10)}</span>
      </h3>
      <table className="table">
        <thead>
          <tr>
            <th>Scope</th>
            <th>Type</th>
            <th className="num">Prior week</th>
            <th className="num">This week</th>
            <th className="num">Change</th>
          </tr>
        </thead>
        <tbody>
          {costRows.map((r) => (
            <tr key={r.scope + r.deliveryType} className={r.changePct != null && r.changePct >= 5 ? "alert" : ""}>
              <td>{r.scope}</td>
              <td>{r.deliveryType}</td>
              <td className="num">{usd(r.priorUsd)}</td>
              <td className="num">{usd(r.currentUsd)}</td>
              <td className={`num ${r.changePct != null && r.changePct > 0 ? "up" : "down"}`}>
                {r.changePct == null ? "-" : `${r.changePct > 0 ? "+" : ""}${r.changePct.toFixed(1)}%`}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
