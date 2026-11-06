import { describe, expect, it } from "vitest";
import { anyLever, beyondPlan, combineLevers } from "./levers";
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

describe("beyondPlan", () => {
  const plan = { ...NO_LEVERS, batch: 0.4, closer: 0.2 };

  it("keeps a what-if that goes beyond the approved plan (Luis models 30% on an approved 20%)", () => {
    expect(beyondPlan({ ...NO_LEVERS, closer: 0.3 }, [plan])).toEqual({ ...NO_LEVERS, closer: 0.3 });
  });

  it("clears a what-if once the plan covers it (Dana approves the 30%)", () => {
    const what = { ...NO_LEVERS, closer: 0.3, cap: true };
    expect(beyondPlan(what, [{ ...plan, closer: 0.3 }])).toEqual({ ...NO_LEVERS, cap: true });
    expect(anyLever(beyondPlan({ ...NO_LEVERS, batch: 0.4 }, [plan]))).toBe(false);
  });

  it("needs every modeled region's plan to cover a lever", () => {
    expect(beyondPlan({ ...NO_LEVERS, closer: 0.2 }, [plan, NO_LEVERS]).closer).toBe(0.2);
  });

  it("treats everything as a what-if without a plan, and never covers a fee cut", () => {
    expect(beyondPlan({ ...NO_LEVERS, closer: 0.2 }, []).closer).toBe(0.2);
    expect(beyondPlan({ ...NO_LEVERS, fee: -0.5 }, [plan]).fee).toBe(-0.5);
  });
});
