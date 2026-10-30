export type DecisionVerb = "propose" | "approve" | "reject" | "execute";

export type ChatCommand =
  | { kind: "decision"; decision: DecisionVerb; actionId: string; reason: string; setting?: number; region?: string }
  | { kind: "status"; actionId?: string; region?: string }
  | { kind: "help" }
  | { kind: "invalid"; message: string };

export const COMMAND_HELP = [
  "/propose A1 [5%] - propose an action, optionally at another setting (regional ops)",
  "Custom packages work the same way by ID, for example /approve P-SE-01 (no setting; Finance decides the whole package).",
  "/approve A1 [region] [5%] [reason] - approve an action for a region, or change an approval's setting (finance)",
  "/reject A1 [region] <reason> - reject an action; a reason is required (finance)",
  "/revoke A1 [region] <reason> - withdraw an approval before it is executed (finance)",
  "/execute A1 - execute an approved action (regional ops)",
  "/status [A1] [region] - show action status and your next steps",
  "Decisions apply to one region (Southeast, Midwest or West). Without one, the region selected in Actions and decisions is used.",
  "/help - show these commands",
];

const ACTION = /^(A[1-6]|P-[A-Z]{1,3}-\d{2,})$/i;
const PERCENT = /^(\d+(?:\.\d+)?)%$/;
const REGIONS = ["Southeast", "Midwest", "West"];

const regionOf = (token: string | undefined) => REGIONS.find((r) => r.toLowerCase() === token?.toLowerCase());

/**
 * Parses a chat line that starts with "/". Commands run directly against the decisions API,
 * never through the model. Returns null for ordinary messages.
 */
export function parseCommand(input: string): ChatCommand | null {
  const text = input.trim();
  if (!text.startsWith("/")) {
    return null;
  }

  const [rawVerb, rawId, ...tokens] = text.slice(1).split(/\s+/);
  const verb = (rawVerb ?? "").toLowerCase();
  const actionId = rawId?.toUpperCase();
  const region = regionOf(tokens[0]);
  const rest = region ? tokens.slice(1) : tokens;
  const percent = rest.length > 0 ? PERCENT.exec(rest[0]) : null;
  const setting = percent && (verb === "propose" || verb === "approve") ? Number(percent[1]) / 100 : undefined;
  const reason = (setting !== undefined ? rest.slice(1) : rest).join(" ").trim();

  if (verb === "help" || verb === "?") {
    return { kind: "help" };
  }
  if (verb === "status") {
    if (rawId && !ACTION.test(rawId)) {
      return { kind: "invalid", message: `Unknown action '${rawId}'. Use A1 to A6 or a package ID such as P-SE-01.` };
    }
    return region ? { kind: "status", actionId, region } : { kind: "status", actionId };
  }

  const decision: DecisionVerb | undefined =
    verb === "revoke" ? "reject" : (["propose", "approve", "reject", "execute"] as const).find((d) => d === verb);
  if (!decision) {
    return { kind: "invalid", message: `Unknown command '/${rawVerb ?? ""}'. Type /help for the list.` };
  }
  if (!actionId || !ACTION.test(actionId)) {
    return { kind: "invalid", message: `Say which action, for example: /${verb} A2${decision === "reject" ? " <reason>" : ""}` };
  }
  if (decision === "reject" && !reason) {
    return { kind: "invalid", message: `A reason is required, for example: /${verb} ${actionId} customer risk` };
  }
  return {
    kind: "decision",
    decision,
    actionId,
    reason,
    ...(setting !== undefined ? { setting } : {}),
    ...(region ? { region } : {}),
  };
}

/** Playbook action and package IDs mentioned in agent text, in order of first mention. */
export function mentionedActions(text: string): string[] {
  const ids = [...text.matchAll(/\b(A[1-6]|P-[A-Z]{1,3}-\d{2,})\b/g)].map((m) => m[1].toUpperCase());
  return [...new Set(ids)];
}
