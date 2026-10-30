import { describe, expect, it } from "vitest";
import { changesQuestion, modelChanges, settingsSummary, type ModelSettings } from "./modelSync";
import { NO_LEVERS } from "./types";

const base: ModelSettings = {
  dieselShift: 0.5,
  duration: 13,
  levers: NO_LEVERS,
  region: "",
  includePlan: true,
};

describe("modelChanges", () => {
  it("lists only what changed, in words", () => {
    const after = {
      ...base,
      duration: 6,
      levers: { ...NO_LEVERS, batch: 0.55, cap: true },
    };
    expect(modelChanges(base, base)).toEqual([]);
    expect(modelChanges(base, after).map((c) => `${c.label}: ${c.from} → ${c.to}`)).toEqual([
      "Lasts: 13 weeks → 6 weeks",
      "Batch Express what-if: none → 55%",
      "Surcharge cap what-if: none → on",
    ]);
  });

  it("builds the question and the summary line", () => {
    const changes = modelChanges(base, { ...base, dieselShift: 0.75 });
    expect(changesQuestion(changes)).toContain("Diesel change +$0.50/gal → +$0.75/gal");
    expect(settingsSummary({ ...base, levers: { ...NO_LEVERS, fee: 1 } }, 2)).toBe(
      "Diesel +$0.50/gal × 13 weeks · approved plan · Express fee +$1.00",
    );
  });
});
