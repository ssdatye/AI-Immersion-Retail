import { forwardRef } from "react";
import { pct, usdCompact } from "../format";
import type { ModelChange } from "../modelSync";
import type { ScenarioResult } from "../types";

export type DrawerView = "model" | "decisions";

interface Props {
  result: ScenarioResult | null;
  loading: boolean;
  error: string | null;
  /** The model settings these outcomes reflect, in words. */
  summary: string;
  /** Model changes not yet shared with the agent. */
  changes: ModelChange[];
  onAskChanges: () => void;
  drawer: DrawerView | null;
  onDrawer: (view: DrawerView | null) => void;
  /** Decisions waiting for the signed-in person. */
  waitingCount: number;
}

/** The quarter's outcomes for the current model settings, pinned to the top of the studio so they never scroll away. */
export const OutcomesBar = forwardRef<HTMLElement, Props>(function OutcomesBar(
  { result, loading, error, summary, changes, onAskChanges, drawer, onDrawer, waitingCount },
  ref,
) {
  const toggle = (view: DrawerView) => onDrawer(drawer === view ? null : view);
  return (
    <section className="obar" ref={ref} aria-labelledby="obar-h">
      <div className="obar-label">
        <h2 id="obar-h">Outcomes this quarter</h2>
        <p>
          {result ? `${result.scope} · ` : ""}
          {summary}
          {loading ? " · updating…" : ""}
        </p>
      </div>
      <div className="obar-kpis" aria-live="polite" aria-busy={loading}>
        {error && <p className="error">{error}</p>}
        {result && (
          <>
            <div className="okpi">
              <span>Fuel shock</span>
              <b className={result.fuelShockQuarterCostUsd > 0 ? "up-bad" : "good-text"}>{usdCompact(result.fuelShockQuarterCostUsd)}</b>
            </div>
            <div className="okpi">
              <span>Offset by plan + levers</span>
              <b>{result.offsetOfFuelShockPct == null ? "-" : pct(result.offsetOfFuelShockPct, 0)}</b>
            </div>
            <div className="okpi">
              <span>Net operating income</span>
              <b className={result.netOperatingIncomeImpactUsd < 0 ? "up-bad" : "good-text"}>
                {result.netOperatingIncomeImpactUsd > 0 ? "+" : ""}
                {usdCompact(result.netOperatingIncomeImpactUsd)}
              </b>
            </div>
            <div className="okpi">
              <span>Express within 3 hours</span>
              <b className={result.guardrail.passed ? "" : "up-bad"}>{pct(result.expressWithin3HoursPct, 1)}</b>
              <span className={`guard ${result.guardrail.passed ? "good-text" : "up-bad"}`}>
                {result.guardrail.passed ? `✓ ≥ ${result.guardrail.thresholdPct}%` : `✗ below ${result.guardrail.thresholdPct}%`}
              </span>
            </div>
          </>
        )}
      </div>
      <div className="obar-side">
        <div className={`obar-status ${changes.length ? "pending" : ""}`} role="status">
          {changes.length === 0 ? (
            "✓ No model changes waiting for the agent"
          ) : (
            <>
              ✎ {changes.length} model {changes.length === 1 ? "change" : "changes"} not shared with the agent
              <button type="button" className="primary" onClick={onAskChanges}>
                Ask the agent about {changes.length === 1 ? "this change" : "these changes"}
              </button>
            </>
          )}
        </div>
        <div className="obar-buttons">
          <button
            type="button"
            className={drawer === "model" ? "active" : ""}
            aria-expanded={drawer === "model"}
            aria-controls="studio-drawer"
            onClick={() => toggle("model")}
          >
            ⚙ {drawer === "model" ? "Hide the model" : "Fine-tune the model"}
          </button>
          <button
            type="button"
            className={drawer === "decisions" ? "active" : waitingCount ? "needs" : ""}
            aria-expanded={drawer === "decisions"}
            aria-controls="studio-drawer"
            onClick={() => toggle("decisions")}
          >
            {waitingCount ? `🔔 Decisions (${waitingCount})` : "Decisions"}
          </button>
        </div>
      </div>
    </section>
  );
});
