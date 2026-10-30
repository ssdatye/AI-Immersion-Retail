import type { Levers } from "./types";

/**
 * Combines lever settings the way the server does: the larger value of each lever wins, and only
 * actions that set a fee affect the fee (so a what-if fee cut is kept).
 */
export function combineLevers(base: Levers, add: Levers): Levers {
  return {
    batch: Math.max(base.batch, add.batch),
    closer: Math.max(base.closer, add.closer),
    split: Math.max(base.split, add.split),
    pickup: Math.max(base.pickup, add.pickup),
    fee: add.fee !== 0 ? Math.max(base.fee, add.fee) : base.fee,
    basket: base.basket || add.basket,
    cap: base.cap || add.cap,
  };
}

const LEVER_KEYS = ["batch", "closer", "split", "pickup", "fee", "basket", "cap"] as const;

/**
 * The what-if levers that go beyond the approved plan, with the rest turned off. A lever is covered when the
 * plan of every modeled region already sets it at least as high, so it adds nothing to the outcomes. A what-if
 * fee cut (negative fee) is never covered.
 */
export function beyondPlan(levers: Levers, floors: Levers[]): Levers {
  const out = { ...levers };
  if (floors.length === 0) {
    return out;
  }
  for (const key of LEVER_KEYS) {
    const v = levers[key];
    const covered =
      typeof v === "boolean"
        ? !v || floors.every((f) => f[key] === true)
        : v === 0 || (v > 0 && floors.every((f) => (f[key] as number) >= v));
    if (covered) {
      (out as Record<string, number | boolean>)[key] = typeof v === "boolean" ? false : 0;
    }
  }
  return out;
}

/** True when any lever is set. */
export const anyLever = (l: Levers) => LEVER_KEYS.some((k) => l[k] !== 0 && l[k] !== false);

export const sameLevers = (a: Levers, b: Levers) => LEVER_KEYS.every((k) => a[k] === b[k]);
