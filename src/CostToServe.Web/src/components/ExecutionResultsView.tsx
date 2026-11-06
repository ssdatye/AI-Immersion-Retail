import { pct, usd, usdCompact } from "../format";
import type { ExecutionResults } from "../types";
import { Bars } from "./Charts";

const zoneRange = (zones: string[]) => {
  const numbers = zones.map((z) => z.match(/\d+$/)?.[0]).filter(Boolean);
  return numbers.length > 1 ? `zones ${numbers[0]}–${numbers.at(-1)}` : zones.join(", ");
};

const signedPct = (v: number) => `${v > 0 ? "+" : ""}${v.toFixed(1)}%`;

/** Modeled pilot-vs-control readout for executed actions (reference demo's "next day" result). */
export function ExecutionResultsView({ results }: { results: ExecutionResults | null }) {
  if (!results) {
    return null;
  }
  if (results.status !== "ok" || !results.expressCostPerOrderUsd) {
    return (
      <div className="results empty">
        <div className="results-title">Execution results</div>
        <div className="muted small">{results.message}</div>
      </div>
    );
  }

  const c = results.expressCostPerOrderUsd;
  return (
    <div className="results">
      <div className="results-title">
        Execution results <span className="muted">· modeled pilot, {results.region}</span>
        <span className="plan-ids">
          {results.executedActions?.map((a) => (
            <span key={a.actionId} className="status st-executed" title={`Executed by ${a.executedBy}${a.setting ? ` · ${a.setting}` : ""}`}>
              {a.actionId} ✓{a.setting ? ` ${a.setting.replace(/^.* /, "")}` : ""}
            </span>
          ))}
        </span>
      </div>
      <div className="results-kpis">
        <span>
          Pilot ({zoneRange(results.pilotZones ?? [])}) Express cost per order <strong>{usd(c.pilotBefore)}</strong> →{" "}
          <strong>{usd(c.pilotAfter)}</strong> <span className={c.pilotChangePct < 0 ? "down" : "up"}>({signedPct(c.pilotChangePct)})</span>
        </span>
        <span>
          Control ({zoneRange(results.controlZones ?? [])}) {usd(c.controlBefore)} → {usd(c.controlAfter)}{" "}
          <span className="muted">({signedPct(c.controlChangePct)}, flat)</span>
        </span>
        <span>
          Weekly saving if scaled across {results.region}: <strong>{usdCompact(results.weeklySavingIfScaledUsd)}</strong>
        </span>
        <span>
          Express ≤3h <strong>{pct(results.expressWithin3HoursPct ?? 0, 1)}</strong> {results.guardrailPassed ? "✔" : "✖"}
        </span>
      </div>
      <Bars
        title="Express cost per order, pilot vs control"
        format={(v) => usd(v)}
        items={[
          { label: "Pilot before", value: c.pilotBefore, color: "#94a3b8" },
          { label: "Pilot after", value: c.pilotAfter, color: "#16a34a" },
          { label: "Control before", value: c.controlBefore, color: "#cbd5e1" },
          { label: "Control after", value: c.controlAfter, color: "#94a3b8" },
        ]}
      />
      <div className="muted small">{results.method}</div>
    </div>
  );
}
