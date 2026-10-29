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
