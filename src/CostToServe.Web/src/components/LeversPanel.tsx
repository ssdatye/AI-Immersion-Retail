import { combineLevers } from "../levers";
import { NO_LEVERS, type Dashboard, type Levers } from "../types";

interface Props {
  dashboard: Dashboard;
  levers: Levers;
  onChange: (levers: Levers) => void;
  region: string;
  onRegion: (region: string) => void;
  /** Approved actions already applied to the outcomes. */
  lockedActions: string[];
  /** Shows the approved action in the actions table (where Finance can revoke it). */
  onLockedClick: (actionId: string) => void;
  /** Each action's levers at its decided setting (proposed or approved); falls back to the playbook setting. */
  actionLevers: Record<string, { levers: Levers; setting: string | null }>;
  /** Shown when modeling several regions: approvals apply per region, so the sliders show what-ifs only. */
  planNote?: string | null;
}

type NumericLever = "batch" | "closer" | "split" | "pickup" | "fee";

const ACTION_NAMES: Record<string, string> = {
  A1: "Batch Express",
  A2: "Closer store",
  A3: "Rural min. basket",
  A4: "Fewer splits",
  A5: "Surcharge cap",
  A6: "Pickup nudge",
};

/** True when every lever an action uses is at least at the action's setting. */
function isApplied(levers: Levers, action: Levers) {
  return (
    (action.batch === 0 || levers.batch >= action.batch) &&
    (action.closer === 0 || levers.closer >= action.closer) &&
    (action.split === 0 || levers.split >= action.split) &&
    (action.pickup === 0 || levers.pickup >= action.pickup) &&
    (action.fee === 0 || levers.fee >= action.fee) &&
    (!action.basket || levers.basket) &&
    (!action.cap || levers.cap)
  );
}

export function LeversPanel({ dashboard, levers, onChange, region, onRegion, lockedActions, onLockedClick, actionLevers, planNote }: Props) {
  const lim = dashboard.leverLimits;
  const leversOf = (id: string) => actionLevers[id]?.levers ?? dashboard.actions.find((a) => a.id === id)?.levers ?? NO_LEVERS;
  // Approved actions set a floor; the sliders show the effective value (approved floor or your what-if, whichever is larger).
  const floor = lockedActions.reduce(
    (acc, id) => combineLevers(acc, leversOf(id)),
    NO_LEVERS,
  );
  const effective = combineLevers(levers, floor);
  const floorSource = (key: keyof Levers) =>
    lockedActions.filter((id) => {
      const a = leversOf(id);
      return a[key] !== 0 && a[key] !== false;
    });
  const sliders: { key: NumericLever; label: string; min: number; max: number; step: number; show: (v: number) => string }[] = [
    { key: "batch", label: "Batch Express into 2-hour windows", min: 0, max: lim.batchMax, step: 0.05, show: (v) => `${Math.round(v * 100)}% of Express` },
    { key: "closer", label: "Fill from a closer store", min: 0, max: lim.closerMax, step: 0.05, show: (v) => `${Math.round(v * 100)}% of orders` },
    { key: "split", label: "Reduce split shipments", min: 0, max: lim.splitMax, step: 0.05, show: (v) => `-${Math.round(v * 100)}%` },
    { key: "pickup", label: "Nudge to Pickup", min: 0, max: lim.pickupMax, step: 0.01, show: (v) => `${Math.round(v * 100)}% shift` },
    { key: "fee", label: "Express fee change", min: lim.feeMin, max: lim.feeMax, step: 0.25, show: (v) => `${v >= 0 ? "+" : "-"}$${Math.abs(v).toFixed(2)}` },
  ];

  const toggle = (action: Levers) => {
    if (isApplied(effective, action)) {
      onChange({
        batch: action.batch ? 0 : levers.batch,
        closer: action.closer ? 0 : levers.closer,
        split: action.split ? 0 : levers.split,
        pickup: action.pickup ? 0 : levers.pickup,
        fee: action.fee ? 0 : levers.fee,
        basket: action.basket ? false : levers.basket,
        cap: action.cap ? false : levers.cap,
      });
    } else {
      onChange(combineLevers(levers, action));
    }
  };

  return (
    <section className="panel">
      <h2>Impact levers</h2>
      {dashboard.caller.role === "FinanceAnalyst" ? (
        <label className="inline">
          Model{" "}
          <select value={region} onChange={(e) => onRegion(e.target.value)}>
            <option value="">Whole network</option>
            {dashboard.regions.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </label>
      ) : (
        <p className="muted">Modeling {dashboard.caller.scope} only (your scope).</p>
      )}
      {planNote && <p className="muted small plan-note">{planNote}</p>}
      {lockedActions.length > 0 && (
        <p className="muted small">
          Approved {lockedActions.join(", ")} {lockedActions.length === 1 ? "is" : "are"} applied automatically at the settings shown. Use the levers to
          try more on top. {dashboard.caller.canApprove ? "To undo one, click it and choose Revoke." : "Only Finance can revoke an approval."}
        </p>
      )}
      <div className="chips" aria-label="Playbook actions">
        {dashboard.actions.map((a) =>
          lockedActions.includes(a.id) ? (
            <button
              key={a.id}
              className="chip locked"
              onClick={() => onLockedClick(a.id)}
              title={`Approved: applied automatically (${a.source}). ${
                dashboard.caller.canApprove ? "Click to find it in Actions and decisions, where you can revoke it." : "Click to see it in Actions and decisions."
              }`}
            >
              ✓ {a.id} {ACTION_NAMES[a.id] ?? ""} · approved{actionLevers[a.id]?.setting ? ` ${actionLevers[a.id].setting}` : ""}
            </button>
          ) : (
            <button
              key={a.id}
              className={isApplied(effective, leversOf(a.id)) ? "chip on" : "chip"}
              onClick={() => toggle(leversOf(a.id))}
              title={a.leversDerivedFromPlaybookText ? "Lever settings derived from playbook text (approximation)" : "Lever settings from the reference demo"}
            >
              {a.id} {ACTION_NAMES[a.id] ?? ""}
              {a.leversDerivedFromPlaybookText ? "*" : ""}
            </button>
          ),
        )}
      </div>
      {sliders.map((s) => {
        const sources = floorSource(s.key);
        const floorValue = sources.length > 0 ? floor[s.key] : null;
        return (
          <label className="slider" key={s.key}>
            <span>
              {s.label} <strong>{s.show(effective[s.key])}</strong>
              {floorValue !== null && (
                <span className="floor-note">
                  {" "}
                  · approved {sources.join(", ")}: {s.show(floorValue)}
                  {effective[s.key] > floorValue ? " + your what-if" : ""}
                </span>
              )}
            </span>
            <input
              type="range"
              min={s.min}
              max={s.max}
              step={s.step}
              value={effective[s.key]}
              className={floorValue !== null ? "has-floor" : undefined}
              onChange={(e) => {
                const value = Number(e.target.value);
                // Below the approved floor there is nothing extra to model; keep the approved value.
                onChange({ ...levers, [s.key]: floorValue !== null && value <= floorValue ? 0 : value });
              }}
            />
          </label>
        );
      })}
      <label className="check" title={floor.basket ? `Approved (${floorSource("basket").join(", ")}): applied automatically` : undefined}>
        <input
          type="checkbox"
          checked={effective.basket}
          disabled={floor.basket}
          onChange={(e) => onChange({ ...levers, basket: e.target.checked })}
        />{" "}
        Minimum basket for Express in rural zips{floor.basket ? ` · approved ${floorSource("basket").join(", ")}` : ""}
      </label>
      <label className="check" title={floor.cap ? `Approved (${floorSource("cap").join(", ")}): applied automatically` : undefined}>
        <input type="checkbox" checked={effective.cap} disabled={floor.cap} onChange={(e) => onChange({ ...levers, cap: e.target.checked })} /> Cap
        carrier fuel surcharge at the contract index{floor.cap ? ` · approved ${floorSource("cap").join(", ")}` : ""}
      </label>
      <p className="muted small">* Lever settings derived from playbook text (approximation).</p>
    </section>
  );
}
