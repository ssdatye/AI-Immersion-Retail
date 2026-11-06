using System.Diagnostics;

namespace CostToServe.Agent.Telemetry;

/// <summary>Telemetry names shared by the agent, its tools and hosts.</summary>
public static class AgentTelemetry
{
    /// <summary>Source for MAF invoke_agent, chat and execute_tool spans (GenAI semantic conventions).</summary>
    public const string SourceName = "CostToServe.Agent";

    /// <summary>Sources a host should subscribe to.</summary>
    public static IReadOnlyList<string> AllSources { get; } = [SourceName, "Experimental.Microsoft.Extensions.AI", "Microsoft.Agents.AI*"];

    // Metadata-only tags: never put prompts, responses or tool payloads on spans.
    public const string CallerRoleTag = "app.caller.role";
    public const string ScopeTag = "app.data.scope";
    public const string ToolOutcomeTag = "app.tool.outcome";
    public const string DecisionTag = "app.decision";

    internal static void TagTool(string role, string scope, string outcome)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetTag(CallerRoleTag, role);
        activity.SetTag(ScopeTag, scope);
        activity.SetTag(ToolOutcomeTag, outcome);
    }
}
