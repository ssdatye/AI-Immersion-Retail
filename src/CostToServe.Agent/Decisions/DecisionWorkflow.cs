using CostToServe.Agent.Security;

namespace CostToServe.Agent.Decisions;

/// <summary>Raised when a decision is valid for the role but not in the action's current state.</summary>
public sealed class DecisionConflictException(string message) : Exception(message);

/// <summary>
/// Decision workflow for a playbook action:
/// Not reviewed -> Proposed (ops) -> Approved or Rejected (finance) -> Executed (ops).
/// Finance may also approve or reject without a proposal, and reject (revoke) an approval
/// before execution. A rejected action can be proposed or approved again. Executed is final
/// until the demo is reset.
/// </summary>
public static class DecisionWorkflow
{
    public static IReadOnlyList<DecisionKind> NextSteps(DecisionKind? latest) => latest switch
    {
        null => [DecisionKind.Propose, DecisionKind.Approve, DecisionKind.Reject],
        DecisionKind.Propose => [DecisionKind.Approve, DecisionKind.Reject],
        DecisionKind.Approve => [DecisionKind.Execute, DecisionKind.Reject],
        DecisionKind.Reject => [DecisionKind.Propose, DecisionKind.Approve],
        DecisionKind.Execute => [],
        _ => [],
    };

    public static bool RoleMay(DemoRole role, DecisionKind kind) => kind switch
    {
        DecisionKind.Approve or DecisionKind.Reject => role == DemoRole.FinanceAnalyst,
        DecisionKind.Propose or DecisionKind.Execute => role == DemoRole.RegionalOps,
        _ => false,
    };

    /// <summary>Decisions this caller can take now, in workflow order.</summary>
    public static IReadOnlyList<DecisionKind> AvailableTo(CallerContext caller, DecisionKind? latest) =>
        NextSteps(latest).Where(k => RoleMay(caller.Role, k)).ToArray();

    /// <summary>
    /// True when the caller may change the setting of the action's current step by repeating it with a
    /// different value: regional ops revising a pending proposal, or finance changing an approval that
    /// has not been executed.
    /// </summary>
    public static bool CanAmend(DemoRole role, DecisionKind? latest) =>
        latest is DecisionKind.Propose or DecisionKind.Approve && RoleMay(role, latest.Value);

    public static string Status(DecisionKind? latest) => latest switch
    {
        null => "Not reviewed",
        DecisionKind.Propose => "Proposed",
        DecisionKind.Approve => "Approved",
        DecisionKind.Reject => "Rejected",
        DecisionKind.Execute => "Executed",
        _ => "Unknown",
    };

    public static string Verb(this DecisionKind kind) => kind.ToString().ToLowerInvariant();
}
