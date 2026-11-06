import type { Levers } from "./types";

/** The model settings the studio sends to the cost engine and, when shared, to the agent as context. */
export interface ModelSettings {
  dieselShift: number;
  duration: number;
  /** What-if levers on top of the approved plan (0 or false means "no what-if"). */
  levers: Levers;
  /** Modeled region; empty means the whole network (or the caller's own scope). */
  region: string;
  includePlan: boolean;
}

export interface ModelChange {
  key: string;
  label: string;
  from: string;
  to: string;
}

const pct = (v: number) => `${Math.round(v * 100)}%`;
const LEVERS: {
  key: keyof Levers;
  label: string;
  show: (v: number | boolean) => string;
}[] = [
  { key: "batch", label: "Batch Express", show: (v) => pct(v as number) },
  {
    key: "closer",
    label: "Fill from a closer store",
    show: (v) => pct(v as number),
  },
  {
    key: "split",
    label: "Reduce split shipments",
    show: (v) => `-${pct(v as number)}`,
  },
  { key: "pickup", label: "Nudge to Pickup", show: (v) => pct(v as number) },
  {
    key: "fee",
    label: "Express fee",
    show: (v) => `${(v as number) >= 0 ? "+" : "-"}$${Math.abs(v as number).toFixed(2)}`,
  },
  {
    key: "basket",
    label: "Rural minimum basket",
    show: (v) => (v ? "on" : "off"),
  },
  { key: "cap", label: "Surcharge cap", show: (v) => (v ? "on" : "off") },
];

const diesel = (v: number) => `${v >= 0 ? "+" : "-"}$${Math.abs(v).toFixed(2)}/gal`;
const isOff = (v: number | boolean) => v === 0 || v === false;

/** The levers that are set, in words, for example "Batch Express 40%". */
export function leverList(levers: Levers): string[] {
  return LEVERS.filter((l) => !isOff(levers[l.key])).map((l) => `${l.label} ${l.show(levers[l.key])}`);
}

/** What changed between the settings last shared with the agent and the current ones, in words. */
export function modelChanges(before: ModelSettings, after: ModelSettings): ModelChange[] {
  const changes: ModelChange[] = [];
  if (before.dieselShift !== after.dieselShift) {
    changes.push({
      key: "diesel",
      label: "Diesel change",
      from: diesel(before.dieselShift),
      to: diesel(after.dieselShift),
    });
  }
  if (before.duration !== after.duration) {
    changes.push({
      key: "duration",
      label: "Lasts",
      from: `${before.duration} weeks`,
      to: `${after.duration} weeks`,
    });
  }
  if (before.region !== after.region) {
    changes.push({
      key: "region",
      label: "Region modeled",
      from: before.region || "whole network",
      to: after.region || "whole network",
    });
  }
  for (const l of LEVERS) {
    const a = before.levers[l.key];
    const b = after.levers[l.key];
    if (a !== b) {
      changes.push({
        key: l.key,
        label: `${l.label} what-if`,
        from: isOff(a) ? "none" : l.show(a),
        to: isOff(b) ? "none" : l.show(b),
      });
    }
  }
  if (before.includePlan !== after.includePlan) {
    changes.push({
      key: "plan",
      label: "Approved plan",
      from: before.includePlan ? "applied" : "not applied",
      to: after.includePlan ? "applied" : "not applied",
    });
  }
  return changes;
}

/** The question the "Ask the agent about these changes" button sends; the settings themselves go along as context. */
export function changesQuestion(changes: ModelChange[]): string {
  const list = changes.map((c) => `${c.label} ${c.from} → ${c.to}`).join("; ");
  return `I changed the model (${list}). What does this do to cost and the fuel-shock offset, and does Express stay within the 3-hour guardrail?`;
}

/** One line describing the current model settings, for the outcomes bar. */
/** The shared basis of the headline outcomes: the diesel scenario and whether the approved plan applies. What-ifs are shown apart. */
export function settingsSummary(s: ModelSettings, planCount: number): string {
  const plan = s.includePlan && planCount > 0 ? "approved plan" : "no plan";
  return [`Diesel ${diesel(s.dieselShift)} × ${s.duration} weeks`, plan].join(" · ");
}
