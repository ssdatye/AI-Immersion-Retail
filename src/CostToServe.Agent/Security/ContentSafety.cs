using System.ClientModel;

namespace CostToServe.Agent.Security;

/// <summary>
/// Requests that the model deployment's content filter blocks before the agent runs, for example a
/// jailbreak attempt caught by prompt shields. The studio and the evaluation treat them as a refusal by
/// policy, not as an error, and show the same answer.
/// </summary>
public static class ContentSafety
{
    public const string ToolName = "content_safety";
    public const string Outcome = "blocked";
    public const string Message = "Blocked by Azure AI Content Safety: the request looks like an attempt to override the agent's rules.";
    public const string Answer =
        "I can't act on that request. Azure AI Content Safety blocked it because it looks like an attempt to override my rules, " +
        "so nothing was changed. Your role and region come from your sign-in. Ask a what-if question, or use the decision buttons.";

    /// <summary>True when the service rejected the prompt with its content filter. Only the error code is read, never echoed.</summary>
    public static bool IsBlocked(ClientResultException ex) =>
        ex.Status == 400 && ex.Message.Contains("content_filter", StringComparison.Ordinal);
}
