using System.Text.Json.Nodes;
using CostToServe.Agent.Security;

namespace CostToServe.Api.Studio;

/// <summary>Links and facts for the Trust Center. Resource IDs come from app settings at runtime, never from the repository.</summary>
public sealed class TrustOptions
{
    public const string SectionName = "Trust";

    /// <summary>Application Insights resource ID, for the transaction search link.</summary>
    public string? AppInsightsResourceId { get; init; }

    /// <summary>Foundry evaluation project resource ID (.../accounts/{account}/projects/{project}).</summary>
    public string? FoundryProjectResourceId { get; init; }

    /// <summary>Public link to the evaluation report.</summary>
    public string? ReportUrl { get; init; }

    public string? FoundryEvaluationsUrl()
    {
        // /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/projects/{project}
        var parts = FoundryProjectResourceId?.Trim('/').Split('/');
        if (parts is not { Length: 10 } || !Guid.TryParse(parts[1], out var subscription))
        {
            return null;
        }

        var encoded = Convert.ToBase64String(subscription.ToByteArray(bigEndian: true)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"https://ai.azure.com/nextgen/r/{encoded},{parts[3]},,{parts[7]},{parts[9]}/build/evaluations";
    }

    /// <summary>Deep link to one evaluation run in the Foundry portal.</summary>
    public string? FoundryRunUrl(string? evalId, string? runId) =>
        FoundryEvaluationsUrl() is { } list && !string.IsNullOrWhiteSpace(evalId) && !string.IsNullOrWhiteSpace(runId)
            ? $"{list}/{Uri.EscapeDataString(evalId)}/run/{Uri.EscapeDataString(runId)}"
            : null;

    public string? AppInsightsSearchUrl() =>
        string.IsNullOrWhiteSpace(AppInsightsResourceId) ? null : $"https://portal.azure.com/#resource{AppInsightsResourceId}/searchV1";
}

public static class TrustEndpoints
{
    /// <summary>
    /// GET /api/trust: the latest evaluation summary, this session's live evidence for the caller's own
    /// answers, the timeline of the latest traced answer, and links to Foundry and Application Insights.
    /// </summary>
    public static object Build(CallerContext caller, StudioServices s, TrustSpanStore spans, TrustOptions options, JsonNode? evaluation)
    {
        var turns = s.Trust.For(caller.Id);
        var answered = turns.Where(t => !t.Errored).ToArray();
        var durations = answered.Select(t => t.DurationMs).Order().ToArray();
        var decisions = s.DecisionLog.ReadAll()
            .Where(d => d.Timestamp >= s.Trust.StartedAt && caller.CanSee(d.EffectiveRegion))
            .ToArray();
        var latest = turns.FirstOrDefault(t => t.TraceId is not null && spans.Get(t.TraceId).Count > 0);
        var everyone = s.Trust.All.Where(t => !t.Errored).ToArray();
        var foundryRun = evaluation?["foundry"];

        return new
        {
            Evaluation = evaluation,
            Session = new
            {
                Since = s.Trust.StartedAt.ToString("O"),
                Answers = answered.Length,
                Errors = turns.Count - answered.Length,
                NumbersQuoted = answered.Sum(t => t.Evidence.Quoted),
                NumbersTraced = answered.Sum(t => t.Evidence.Traced),
                NumbersToReview = answered.Sum(t => t.Evidence.Unsupported.Count),
                RefusedByPolicy = answered.Sum(t => t.Tools.Count(c => c.Outcome is "scope_denied" or "conflict" or ContentSafety.Outcome)),
                DecisionsByPeople = decisions.Length,
                AnswersTraced = answered.Count(t => t.TraceId is not null),
                MedianMs = durations.Length == 0 ? (long?)null : (durations[(durations.Length - 1) / 2] + durations[durations.Length / 2]) / 2,
                Tokens = answered.Sum(t => (t.InputTokens ?? 0) + (t.OutputTokens ?? 0)),
                ToolCalls = answered.Sum(t => t.Tools.Count),
                // Every signed-in user since the reset: counts only, no questions or answers.
                AllUsers = new
                {
                    Users = everyone.Select(t => t.OwnerId).Distinct().Count(),
                    Answers = everyone.Length,
                    NumbersQuoted = everyone.Sum(t => t.Evidence.Quoted),
                    NumbersTraced = everyone.Sum(t => t.Evidence.Traced),
                    RefusedByPolicy = everyone.Sum(t => t.Tools.Count(c => c.Outcome is "scope_denied" or "conflict" or ContentSafety.Outcome)),
                },
            },
            Turns = turns.Take(25).Select(t => new
            {
                At = t.At.ToString("O"),
                Who = t.DisplayName,
                t.Question,
                Tools = t.Tools.Select(c => new { c.Name, c.Outcome, c.Message, Decision = c.DecisionStatus }),
                t.Evidence.Quoted,
                t.Evidence.Traced,
                t.Evidence.Unsupported,
                t.DurationMs,
                t.TraceId,
                t.Errored,
            }),
            LastTrace = latest is null ? null : new { latest.TraceId, latest.Question, Spans = spans.Get(latest.TraceId) },
            Links = new
            {
                FoundryEvaluations = options.FoundryEvaluationsUrl(),
                FoundryRun = options.FoundryRunUrl((string?)foundryRun?["evalId"], (string?)foundryRun?["runId"]),
                Architecture = "/architecture",
                AppInsightsSearch = options.AppInsightsSearchUrl(),
                Report = options.ReportUrl,
            },
            Controls = new
            {
                Model = s.Foundry.Deployment,
                ModelViaGateway = s.Foundry.Endpoint?.Contains("azure-api.net", StringComparison.OrdinalIgnoreCase) ?? false,
                ContentFilter = "Microsoft.DefaultV2",
                s.DataMode,
            },
        };
    }

    /// <summary>Reads the evaluation summary written by evals/report.py; null when missing or unreadable.</summary>
    public static JsonNode? LoadEvaluation(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
