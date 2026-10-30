import type { Dashboard } from "../types";
import { LineChart, type Series } from "./Charts";
import { signed } from "../format";

const COLORS = ["#c2410c", "#2563eb", "#64748b"];

interface Props {
  dashboard: Dashboard;
  dieselShift: number;
  duration: number;
  onShift: (value: number) => void;
  onDuration: (value: number) => void;
  onPreset: (shift: number, duration: number) => void;
}

export function FuelPanel({ dashboard, dieselShift, duration, onShift, onDuration, onPreset }: Props) {
  const { fuel, leverLimits: lim } = dashboard;
  const days = fuel[0]?.daily ?? [];
  const spike = days.findIndex((d) => d.date === dashboard.period.currentWeek.slice(0, 10));
  const outlookLength = fuel[0]?.outlook.length ?? 0;

  const series: Series[] = fuel.flatMap((f, i) => [
    { name: `${f.region} diesel`, values: f.daily.map((d) => d.diesel), color: COLORS[i % COLORS.length] },
    {
      name: `${f.region} outlook`,
      values: [...Array(days.length - 1).fill(null), f.daily.at(-1)?.diesel ?? null, ...f.outlook.map((o) => o.base)],
      color: COLORS[i % COLORS.length],
      dashed: true,
    },
  ]);
  const band =
    fuel.length === 1
      ? { start: days.length, low: fuel[0].outlook.map((o) => o.low), high: fuel[0].outlook.map((o) => o.high), color: COLORS[0] }
      : undefined;

  return (
    <section className="panel">
      <h2>Fuel market</h2>
      <div className="fuel-kpis">
        {fuel.map((f, i) => (
          <div className="fuel-kpi" key={f.region}>
            <span className="dot" style={{ background: COLORS[i % COLORS.length] }} />
            <div>
              <div className="muted">{f.region} diesel</div>
              <strong>${f.currentWeekDiesel.toFixed(2)}</strong>
              <span className={f.currentWeekDiesel >= f.priorWeekDiesel ? "up" : "down"}>
                {" "}
                {signed(f.currentWeekDiesel - f.priorWeekDiesel)} wk/wk
              </span>
            </div>
          </div>
        ))}
      </div>
      <LineChart
        title="Diesel, daily and 13-week outlook"
        series={series}
        band={band}
        marker={spike > 0 ? { index: spike, text: "Supply events" } : undefined}
        labels={[
          { index: 0, text: days[0]?.date.slice(5) ?? "" },
          { index: days.length - 1, text: days.at(-1)?.date.slice(5) ?? "" },
          { index: days.length + outlookLength - 1, text: "outlook" },
        ]}
        format={(v) => `$${v.toFixed(2)}`}
      />
      <ul className="events">
        {dashboard.events.map((e) => (
          <li key={e.date + e.eventType}>
            <span className="muted">{e.date.slice(5)}</span> {e.eventType} <span className="muted">({e.region})</span>{" "}
            <strong>{e.effect}</strong> <span className="tag">{e.credibility}</span>
          </li>
        ))}
      </ul>

      <h3>What-if: diesel change</h3>
      <p className="muted small">Added to this week's price, which already includes the Oct 19 jump.</p>
      <div className="presets">
        {dashboard.presets.map((p) => (
          <button key={p.id} title={p.description} onClick={() => onPreset(p.dieselShift, p.durationWeeks)}>
            {p.label}
          </button>
        ))}
        <button onClick={() => onPreset(0, 8)}>Reset</button>
      </div>
      <label className="slider">
        <span>
          Diesel change <strong>{signed(dieselShift)} $/gal</strong>
        </span>
        <input
          type="range"
          min={lim.dieselShiftMin}
          max={lim.dieselShiftMax}
          step={0.05}
          value={dieselShift}
          onChange={(e) => onShift(Number(e.target.value))}
        />
      </label>
      <label className="slider">
        <span>
          Lasts <strong>{duration} weeks</strong> of the 13-week quarter
        </span>
        <input
          type="range"
          min={lim.durationMinWeeks}
          max={lim.durationMaxWeeks}
          step={1}
          value={duration}
          onChange={(e) => onDuration(Number(e.target.value))}
        />
      </label>
    </section>
  );
}
