export interface Series {
  name: string;
  values: (number | null)[];
  color: string;
  dashed?: boolean;
}

interface LineChartProps {
  title: string;
  series: Series[];
  band?: { start: number; low: number[]; high: number[]; color: string };
  labels?: { index: number; text: string }[];
  marker?: { index: number; text: string };
  format: (v: number) => string;
  height?: number;
}

/** Small dependency-free SVG line chart with an optional range band and marker. */
export function LineChart({ title, series, band, labels = [], marker, format, height = 170 }: LineChartProps) {
  const width = 520;
  const pad = { l: 46, r: 8, t: 18, b: 22 };
  const all = series.flatMap((s) => s.values.filter((v): v is number => v != null));
  if (band) {
    all.push(...band.low, ...band.high);
  }
  if (all.length === 0) {
    return null;
  }
  let min = Math.min(...all);
  let max = Math.max(...all);
  const margin = (max - min) * 0.1 || 0.5;
  min -= margin;
  max += margin;
  const n = Math.max(...series.map((s) => s.values.length), band ? band.start + band.low.length : 0);
  const x = (i: number) => pad.l + ((width - pad.l - pad.r) * i) / Math.max(1, n - 1);
  const y = (v: number) => pad.t + (height - pad.t - pad.b) * (1 - (v - min) / (max - min));

  const path = (values: (number | null)[], offset = 0) =>
    values.reduce((d, v, i) => (v == null ? d : `${d}${d === "" ? "M" : "L"}${x(i + offset).toFixed(1)},${y(v).toFixed(1)}`), "");

  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" role="img" aria-label={title}>
      <title>{title}</title>
      {[0, 1, 2, 3].map((k) => {
        const v = min + ((max - min) * k) / 3;
        return (
          <g key={k}>
            <line x1={pad.l} x2={width - pad.r} y1={y(v)} y2={y(v)} className="grid" />
            <text x={pad.l - 6} y={y(v) + 4} className="axis" textAnchor="end">
              {format(v)}
            </text>
          </g>
        );
      })}
      {band && (
        <path
          d={`${path(band.high, band.start)}${band.low
            .map((_, i) => `L${x(band.start + band.low.length - 1 - i).toFixed(1)},${y(band.low[band.low.length - 1 - i]).toFixed(1)}`)
            .join("")}Z`}
          fill={band.color}
          opacity={0.18}
        />
      )}
      {marker && (
        <g>
          <line x1={x(marker.index)} x2={x(marker.index)} y1={pad.t} y2={height - pad.b} className="marker" />
          <text x={x(marker.index) + 4} y={pad.t + 10} className="marker-text">
            {marker.text}
          </text>
        </g>
      )}
      {series.map((s) => (
        <path key={s.name} d={path(s.values)} fill="none" stroke={s.color} strokeWidth={2.2} strokeDasharray={s.dashed ? "5 4" : undefined} />
      ))}
      {labels.map((l) => (
        <text key={l.index} x={x(l.index)} y={height - 6} className="axis" textAnchor="middle">
          {l.text}
        </text>
      ))}
    </svg>
  );
}

interface BarsProps {
  title: string;
  items: { label: string; value: number; color: string }[];
  format: (v: number) => string;
}

/** Horizontal bars, scaled from the smallest value so small differences stay visible. */
export function Bars({ title, items, format }: BarsProps) {
  const max = Math.max(...items.map((i) => i.value));
  const min = Math.min(...items.map((i) => i.value)) * 0.97;
  return (
    <div className="bars" role="img" aria-label={title}>
      {items.map((item) => (
        <div className="bar-row" key={item.label}>
          <span className="bar-label">{item.label}</span>
          <span className="bar-track">
            <span className="bar-fill" style={{ width: `${max === min ? 100 : 8 + (92 * (item.value - min)) / (max - min)}%`, background: item.color }} />
          </span>
          <span className="bar-value">{format(item.value)}</span>
        </div>
      ))}
    </div>
  );
}
