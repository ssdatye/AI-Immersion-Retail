import { describe, expect, it } from "vitest";
import { combineLevers } from "./levers";
import { NO_LEVERS } from "./types";

describe("combineLevers", () => {
  it("takes the larger value of each lever and ORs switches", () => {
    const a1 = { ...NO_LEVERS, batch: 0.35 };
    const a2 = { ...NO_LEVERS, closer: 0.2 };
    const a5 = { ...NO_LEVERS, cap: true };
    expect(combineLevers(combineLevers(combineLevers(NO_LEVERS, a1), a2), a5)).toEqual({ ...NO_LEVERS, batch: 0.35, closer: 0.2, cap: true });
    expect(combineLevers({ ...NO_LEVERS, batch: 0.5 }, a1).batch).toBe(0.5);
  });

  it("keeps a what-if fee cut unless an action sets a fee", () => {
    expect(combineLevers({ ...NO_LEVERS, fee: -0.5 }, { ...NO_LEVERS, closer: 0.2 }).fee).toBe(-0.5);
    expect(combineLevers({ ...NO_LEVERS, fee: -0.5 }, { ...NO_LEVERS, fee: 1 }).fee).toBe(1);
  });
});
