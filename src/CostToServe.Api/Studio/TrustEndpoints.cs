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

    /// <summary>Token prices used to show what each model call and answer cost (list price unless configured).</summary>
    public TokenPricing Pricing { get; init; } = new();

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

/// <summary>
/// Token prices for the model deployment, in USD per million tokens. Defaults are the Azure list price for
/// gpt-4.1-mini Global Standard in East US 2 (Azure Retail Prices API, October 30, 2026); override in Trust:Pricing.
/// </summary>
public sealed class TokenPricing
{
    public string Model { get; init; } = "gpt-4.1-mini";

    public double InputPerMillionUsd { get; init; } = 0.40;

    public double CachedInputPerMillionUsd { get; init; } = 0.10;

    public double OutputPerMillionUsd { get; init; } = 1.60;

    public string Source { get; init; } = "Azure list price (Retail Prices API): gpt-4.1-mini Global Standard, East US 2";

    public string AsOf { get; init; } = "2026-10-30";

    /// <summary>Daily answer volume for the monthly projection.</summary>
    public int ProjectionAnswersPerDay { get; init; } = 1000;

    /// <summary>Cost of one call or answer. Cached input tokens are part of the input count and billed at the cached rate.</summary>
    public double Cost(long? input, long? cachedInput, long? output)
    {
        var cached = Math.Min(cachedInput ?? 0, input ?? 0);
        return (((input ?? 0) - cached) * InputPerMillionUsd + cached * CachedInputPerMillionUsd + (output ?? 0) * OutputPerMillionUsd) / 1_000_000;
    }
}

public static class TrustEndpoints
{
    private const int ProjectionDays = 30;

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
        var pricing = options.Pricing;
        double TurnCost(TrustTurn t) => pricing.Cost(t.InputTokens, t.CachedInputTokens, t.OutputTokens);
        var sessionCost = answered.Sum(TurnCost);

        // The evaluation run's agent tokens give a stable average cost per answer for the projection.
        var evalTokens = evaluation?["tokens"];
        var evalAnswers = (int?)evaluation?["cases"] ?? 0;
        var evalCost = evalTokens is null ? (double?)null
            : pricing.Cost((long?)evalTokens["input"], (long?)evalTokens["cachedInput"], (long?)evalTokens["output"]);
        var perAnswer = evalCost is { } ec && evalAnswers > 0 ? ec / evalAnswers
            : answered.Length > 0 ? sessionCost / answered.Length : (double?)null;

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
                InputTokens = answered.Sum(t => t.InputTokens ?? 0),
                CachedInputTokens = answered.Sum(t => t.CachedInputTokens ?? 0),
                OutputTokens = answered.Sum(t => t.OutputTokens ?? 0),
                CostUsd = sessionCost,
                CostPerAnswerUsd = answered.Length == 0 ? (double?)null : sessionCost / answered.Length,
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
                t.InputTokens,
                t.OutputTokens,
                CostUsd = t.Errored ? (double?)null : TurnCost(t),
                t.TraceId,
                t.Errored,
            }),
            LastTrace = latest is null ? null : new
            {
                latest.TraceId,
                latest.Question,
                // Model calls carry their own token counts; tools run the cost engine and use no tokens.
                Spans = spans.Get(latest.TraceId).Select(sp => new
                {
                    sp.Kind,
                    sp.Label,
                    sp.StartMs,
                    sp.DurationMs,
                    sp.Error,
                    sp.InputTokens,
                    sp.CachedInputTokens,
                    sp.OutputTokens,
                    CostUsd = sp.Kind == "model" && sp.InputTokens is not null ? pricing.Cost(sp.InputTokens, sp.CachedInputTokens, sp.OutputTokens)
                        : sp.Kind == "tool" ? 0 : (double?)null,
                }),
                latest.InputTokens,
                latest.CachedInputTokens,
                latest.OutputTokens,
                CostUsd = TurnCost(latest),
            },
            Pricing = new
            {
                pricing.Model,
                pricing.InputPerMillionUsd,
                pricing.CachedInputPerMillionUsd,
                pricing.OutputPerMillionUsd,
                pricing.Source,
                pricing.AsOf,
                EvaluationCostUsd = evalCost,
                Projection = perAnswer is not { } pa ? null : new
                {
                    AnswersPerDay = pricing.ProjectionAnswersPerDay,
                    Days = ProjectionDays,
                    PerAnswerUsd = pa,
                    MonthlyUsd = pa * pricing.ProjectionAnswersPerDay * ProjectionDays,
                    Basis = evalCost is not null ? "evaluation run average" : "this session's average",
                },
            },
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
