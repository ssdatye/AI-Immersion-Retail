export type Persona = "dana" | "luis";

export interface Levers {
  batch: number;
  closer: number;
  split: number;
  pickup: number;
  fee: number;
  basket: boolean;
  cap: boolean;
}

export const NO_LEVERS: Levers = { batch: 0, closer: 0, split: 0, pickup: 0, fee: 0, basket: false, cap: false };

export interface Caller {
  displayName: string;
  title: string;
  role: "FinanceAnalyst" | "RegionalOps";
  scope: string;
  canApprove: boolean;
}

export interface Preset {
  id: string;
  label: string;
  dieselShift: number;
  durationWeeks: number;
  description: string;
}

export interface FuelRegion {
  region: string;
  currentWeekDiesel: number;
  priorWeekDiesel: number;
  daily: { date: string; diesel: number }[];
  outlook: { weekStart: string; low: number; base: number; high: number }[];
}

export interface CostRow {
  scope: string;
  deliveryType: string;
  priorUsd: number | null;
  currentUsd: number | null;
  changePct: number | null;
}

export interface Dashboard {
  caller: Caller;
  period: { dataStart: string; dataEnd: string; currentWeek: string; priorWeek: string };
  seedVersion: string;
  regions: string[];
  fuel: FuelRegion[];
  events: { date: string; eventType: string; region: string; effect: string; credibility: string }[];
  costPerOrder: CostRow[];
  presets: Preset[];
  suggestions: string[];
  leverLimits: {
    batchMax: number;
    closerMax: number;
    splitMax: number;
    pickupMax: number;
    feeMin: number;
    feeMax: number;
    dieselShiftMin: number;
    dieselShiftMax: number;
    durationMinWeeks: number;
    durationMaxWeeks: number;
  };
  actions: { id: string; levers: Levers; leversDerivedFromPlaybookText: boolean; source: string }[];
}

export interface GuardrailFinding {
  code: string;
  severity: "Blocking" | "ApprovalRequired" | "Warning" | "Constraint";
  message: string;
}

export interface Guardrail {
  passed: boolean;
  expressWithin3HoursPct: number;
  thresholdPct: number;
  findings: GuardrailFinding[];
}

export interface ScenarioResult {
  scope: string;
  dieselChangeUsdPerGallon: number;
  durationWeeks: number;
  fuelShockQuarterCostUsd: number;
  scenarioQuarterCostUsd: number;
  netOperatingIncomeImpactUsd: number;
  offsetOfFuelShockPct: number | null;
  weeklyCostUsd: { baseline: number; withFuelShock: number; withLevers: number; withFuelShockAndLevers: number };
  expressWithin3HoursPct: number;
  promiseKeptPct: number;
  availabilityPct: number;
  guardrail: Guardrail;
  method: string;
  decidedPlan?: Record<string, string[]> | null;
}

export type ActionStatus = "Not reviewed" | "Proposed" | "Approved" | "Rejected" | "Executed";

/** One action's decision state in one region. Decisions apply to one region at a time. */
export interface RegionDecision {
  region: string;
  status: ActionStatus;
  availableDecisions: string[];
  setting?: ActionSetting | null;
  canAmendSetting: boolean;
  latestDecisionBy?: string | null;
  latestDecisionAt?: string | null;
  latestDecisionReason?: string | null;
}

export interface PlaybookAction {
  actionId: string;
  action: string;
  effect: string;
  customerImpact: string;
  approver: string;
  guardrailRule: string;
  leverMapping: string;
  leverSource: string | null;
  estimatedWeeklySavingUsd: number | null;
  guardrailPassed: boolean | null;
  guardrailFindings: string[];
  /** Status and the fields below are for the playbook's decision region. */
  status: ActionStatus;
  region?: string | null;
  latestDecision: string;
  latestDecisionBy: string | null;
  latestDecisionAt: string | null;
  latestDecisionReason: string | null;
  /** Decisions the signed-in user can take now (server-computed from role and workflow). */
  availableDecisions: string[];
  /** Levers at the currently decided setting (proposed or approved), else the playbook setting. */
  levers?: Levers | null;
  /** The adjustable lever (A1, A2, A4, A6); null or absent for on/off actions. Values are fractions. */
  setting?: ActionSetting | null;
  /** True when the caller can change the setting of the current step (ops: their proposal; finance: an approval). */
  canAmendSetting: boolean;
  /** Decision state in every region the caller can see. */
  byRegion: RegionDecision[];
}

export interface ActionSetting {
  lever: string;
  label: string;
  value: number;
  default: number;
  min: number;
  max: number;
  step: number;
  isDefault: boolean;
  text: string;
}

export interface Playbook {
  decisionRegion?: string | null;
  savingsScope: string;
  savingsNote: string;
  actions: PlaybookAction[];
}

export interface DecisionRecord {
  timestamp: string;
  actionId: string;
  actor: string;
  decision: string;
  reason: string;
  setting: number | null;
  region: string;
}

export interface ConfirmationCard {
  id: string;
  actionId: string;
  actionName: string;
  decision: string;
  actor: string;
  reason: string;
  estimatedWeeklySavingUsd: number | null;
  guardrailNotes: string[];
  setting?: string | null;
  region?: string | null;
}

export interface ExecutionResults {
  status: "none" | "ok";
  message?: string;
  region?: string;
  executedActions?: { actionId: string; executedBy: string; executedAt: string; setting?: string | null }[];
  pilotZones?: string[];
  controlZones?: string[];
  expressCostPerOrderUsd?: {
    pilotBefore: number;
    pilotAfter: number;
    pilotChangePct: number;
    controlBefore: number;
    controlAfter: number;
    controlChangePct: number;
  };
  weeklySavingIfScaledUsd?: number;
  expressWithin3HoursPct?: number;
  guardrailPassed?: boolean;
  method?: string;
}
