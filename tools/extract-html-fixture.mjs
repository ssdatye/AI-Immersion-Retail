// Generates the golden parity fixture for the C# cost engine by running the
// original cost-engine functions from the reference demo HTML. Only the
// constants and the pure engine functions are evaluated; no UI code runs.
//
// Usage: node tools/extract-html-fixture.mjs
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const htmlPath = join(root, "docs/reference/walmart-v3-cost-to-serve-demo.html");
const outPath = join(root, "tests/CostToServe.Core.Tests/Fixtures/html-engine-golden.json");

const html = readFileSync(htmlPath, "utf8");
const dataJson = html.match(/<script id="data" type="application\/json">(.*?)<\/script>/s)[1];
const D = JSON.parse(dataJson);

const slice = (start, end) => {
  const a = html.indexOf(start);
  const b = html.indexOf(end, a);
  if (a < 0 || b < 0) throw new Error(`Engine marker not found: ${start} .. ${end}`);
  return html.slice(a, b);
};
const engineSource =
  slice("const R=", "const css=") +
  slice("function surch(", "/* ---------- SVG charts");

const ctx = vm.createContext({ D });
vm.runInContext(`${engineSource}
globalThis.__engine = { R, T, unit, tot, weekly, scenario, guard };`, ctx, { timeout: 2000 });
const { R, T, unit, tot, weekly, scenario, guard } = ctx.__engine;

const pickUnit = (u) => ({ fuel: u.fuel, labor: u.labor, carrier: u.carrier, vehicle: u.veh, total: tot(u) });
const pickWeekly = (w) => ({ cost: w.cost, fuel: w.fuel, orders: w.orders, fee: w.fee, incentive: w.incent });
const normLevers = (L) => ({
  batch: L.batch ?? 0, closer: L.closer ?? 0, split: L.split ?? 0, pickup: L.pickup ?? 0,
  fee: L.fee ?? 0, basket: !!L.basket, cap: !!L.cap,
});

// Mirrors weekly(): lever settings -> unit() modifiers.
const modifiersFor = (L) => ({
  mpoMult: 1 - 0.30 * (L.closer || 0), oprMultEx: 1 + 0.35 * (L.batch || 0),
  splitMult: 1 - (L.split || 0), cap: !!L.cap,
});

const leverSets = [
  {},
  { batch: 0.35 },
  { closer: 0.2 },
  { batch: 0.35, closer: 0.2 },
  { split: 0.4 },
  { split: 0.6 },
  { pickup: 0.04 },
  { pickup: 0.1 },
  { fee: 1 },
  { fee: -0.5 },
  { fee: 3 },
  { basket: true },
  { cap: true },
  { batch: 0.6, closer: 0.4, split: 0.6, pickup: 0.1, fee: 3, basket: true, cap: true },
  // Plan candidates from the HTML plan() function.
  { cap: true, split: 0.2 },
  { cap: true, split: 0.3, closer: 0.1 },
  { cap: true, split: 0.4, closer: 0.2 },
  { cap: true, split: 0.4, closer: 0.2, batch: 0.25 },
  { cap: true, split: 0.4, closer: 0.2, batch: 0.25, pickup: 0.04 },
  { cap: true, split: 0.4, closer: 0.2, batch: 0.25, pickup: 0.04, fee: 1 },
];
const shocks = [
  { shift: 0.5, duration: 13 }, { shift: 0.5, duration: 8 }, { shift: 0.45, duration: 6 },
  { shift: 0.8, duration: 10 }, { shift: -0.15, duration: 13 }, { shift: 0, duration: 8 },
  { shift: 1, duration: 1 }, { shift: -0.5, duration: 4 },
];

const unitCases = [];
for (const week of Object.keys(D.base)) {
  for (const region of R) {
    for (const type of T) {
      const s = D.base[week][region][type];
      for (const L of [{}, { batch: 0.35, closer: 0.2, split: 0.4, cap: true }]) {
        for (const dieselShift of [0, 0.5]) {
          const diesel = s.die + dieselShift;
          unitCases.push({
            week: Number(week), region, type, levers: normLevers(L), diesel,
            expected: pickUnit(unit(type, s, diesel, modifiersFor(L))),
          });
        }
      }
    }
  }
}

const weeklyCases = [];
for (const L of leverSets) {
  for (const shift of [0, 0.5, -0.15]) {
    for (const regions of [null, ["Southeast"], ["Midwest", "West"]]) {
      weeklyCases.push({
        levers: normLevers(L), shift, regions,
        expected: pickWeekly(weekly(L, shift, regions ?? undefined)),
      });
    }
  }
}

const scenarioCases = [];
for (const L of leverSets) {
  for (const { shift, duration } of shocks) {
    const s = scenario(L, shift, duration);
    scenarioCases.push({
      levers: normLevers(L), shift, duration,
      expected: {
        baseCost: s.base.cost, shockCost: s.shockW.cost, bothCost: s.bothW.cost,
        shockQuarter: s.shockQ, scenarioQuarter: s.scenQ, netOperatingIncome: s.net,
        offset: s.offset, expressWithin3HoursPct: s.under3, promisePct: s.promise, availabilityPct: s.avail,
      },
    });
  }
}

const guardCases = leverSets.map((L) => ({ levers: normLevers(L), expected: guard(L) }));

// Generalizes the HTML "story numbers" (prior week 5 -> current week 6) to every region and type.
const varianceCases = [];
for (const region of R) {
  for (const type of T) {
    const p = D.base["5"][region][type];
    const c = D.base["6"][region][type];
    const c0 = tot(unit(type, p, p.die));
    const c1 = tot(unit(type, p, c.die));
    const c2 = tot(unit(type, { ...p, opr: c.opr, mpo: c.mpo }, c.die));
    const c3 = tot(unit(type, c, c.die));
    varianceCases.push({ region, type, expected: { prior: c0, afterFuel: c1, afterDensity: c2, current: c3 } });
  }
}

// Action savings: seBase.cost - w.cost - w.incent + w.fee, using the HTML ACT lever settings.
const seBase = weekly({}, 0, ["Southeast"]);
const actionCases = [
  { id: "A1", levers: { batch: 0.35 } },
  { id: "A2", levers: { closer: 0.2 } },
  { id: "A3", levers: { basket: true } },
  { id: "A1+A2", levers: { batch: 0.35, closer: 0.2 } },
].map(({ id, levers }) => {
  const w = weekly(levers, 0, ["Southeast"]);
  return { id, levers: normLevers(levers), region: "Southeast", expectedWeeklySaving: seBase.cost - w.cost - w.incent + w.fee };
});

// Pilot vs control (reference "Next day: the result" step): Southeast zones 1-3 are the pilot,
// zones 4-7 the control; Express cost per order at current-week zone volumes; control assumed flat (+0.2%).
const zoneCost = (zones, L) => {
  let c = 0, o = 0;
  for (const z of zones) {
    const m = { mpoMult: 1 - 0.30 * (L.closer || 0), oprMultEx: 1 + 0.35 * (L.batch || 0) };
    c += tot(unit("Express", z.w6, z.w6.die, m)) * z.w6.orders;
    o += z.w6.orders;
  }
  return c / o;
};
const pilotZones = D.zonesSE.slice(0, 3), controlZones = D.zonesSE.slice(3);
const pilotCases = [{ batch: 0.35 }, { closer: 0.2 }, { batch: 0.35, closer: 0.2 }, {}].map((L) => ({
  levers: normLevers(L),
  expected: {
    pilotBefore: zoneCost(pilotZones, {}),
    pilotAfter: zoneCost(pilotZones, L),
    controlBefore: zoneCost(controlZones, {}),
    controlAfter: zoneCost(controlZones, {}) * (1 + 0.002),
  },
}));

const fixture = {
  description: "Golden outputs from the original HTML cost engine. Regenerate with: node tools/extract-html-fixture.mjs",
  source: {
    file: relative(root, htmlPath).replaceAll("\\", "/"),
    sha256: createHash("sha256").update(readFileSync(htmlPath)).digest("hex"),
  },
  baseWeeks: D.base,
  zonesSE: D.zonesSE,
  unitCases, weeklyCases, scenarioCases, guardCases, varianceCases, actionCases, pilotCases,
};

writeFileSync(outPath, JSON.stringify(fixture, null, 1) + "\n");
console.log(`Wrote ${relative(root, outPath)}: ${unitCases.length} unit, ${weeklyCases.length} weekly, ` +
  `${scenarioCases.length} scenario, ${guardCases.length} guard, ${varianceCases.length} variance, ${actionCases.length} action, ` +
  `${pilotCases.length} pilot cases.`);
