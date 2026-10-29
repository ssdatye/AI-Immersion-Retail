using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using CostToServe.Agent.Evidence;
using OpenTelemetry;

namespace CostToServe.Api.Studio;

/// <summary>A tool call in one chat turn: name, outcome and result (kept in memory for the evidence check).</summary>
public sealed record TurnToolCall(string Name, string Outcome, JsonNode? Result)
{
    /// <summary>The tool's refusal message, for example a scope or guardrail rule; null when the call succeeded.</summary>
    public string? Message => Outcome == "ok" ? null : (string?)(Result as JsonObject)?["message"];

    /// <summary>"recorded" or "not_recorded" for log_decision calls that reached the confirmation step.</summary>
    public string? DecisionStatus => Name == "log_decision" && Outcome == "ok" ? (string?)(Result as JsonObject)?["status"] : null;
}

/// <summary>One agent answer, as shown in the Trust Center. No tool payloads are kept.</summary>
public sealed record TrustTurn(
    string OwnerId,
    string DisplayName,
    DateTimeOffset At,
    string Question,
    IReadOnlyList<(string Name, string Outcome, string? Message, string? DecisionStatus)> Tools,
    AnswerEvidence Evidence,
    long DurationMs,
    long? InputTokens,
    long? OutputTokens,
    string? TraceId,
    bool Errored);

/// <summary>
/// Recent agent answers in this server process, for the Trust Center. Bounded and in memory: it
/// restarts with the app. Each caller sees only their own answers.
/// </summary>
public sealed class TrustLog
{
    private const int MaxTurns = 300;
    private readonly ConcurrentQueue<TrustTurn> _turns = new();

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void Add(TrustTurn turn)
    {
        _turns.Enqueue(turn);
        while (_turns.Count > MaxTurns && _turns.TryDequeue(out _))
        {
        }
    }

    /// <summary>Every user's turns, for totals only (never question text).</summary>
    public IReadOnlyList<TrustTurn> All => _turns.ToArray();

    public IReadOnlyList<TrustTurn> For(string ownerId) =>
        _turns.Where(t => t.OwnerId == ownerId).OrderByDescending(t => t.At).ToArray();
}

/// <summary>One span of a chat turn's trace, reduced to what the Trust Center timeline shows.</summary>
public sealed record TraceSpan(string Kind, string Label, double StartMs, double DurationMs, bool Error);

/// <summary>
/// Keeps the spans of recent traces in memory so the Trust Center can show how an answer was produced
/// (request, agent, model calls, tools, data). Metadata only: no prompts, responses or tool payloads.
/// The same spans also go to Application Insights.
/// </summary>
public sealed class TrustSpanStore : BaseProcessor<Activity>
{
    private const int MaxTraces = 100;
    private readonly ConcurrentDictionary<string, ConcurrentBag<(DateTime Start, string Kind, string Label, TimeSpan Duration, bool Error)>> _traces = new();
    private readonly ConcurrentQueue<string> _order = new();

    public override void OnEnd(Activity activity)
    {
        var kind = Classify(activity);
        if (kind is null)
        {
            return;
        }

        var bag = _traces.GetOrAdd(activity.TraceId.ToHexString(), id =>
        {
            _order.Enqueue(id);
            while (_order.Count > MaxTraces && _order.TryDequeue(out var old))
            {
                _traces.TryRemove(old, out _);
            }

            return [];
        });
        bag.Add((activity.StartTimeUtc, kind, Label(activity, kind), activity.Duration, activity.Status == ActivityStatusCode.Error));
    }

    /// <summary>The trace's spans in start order, with times relative to the first span.</summary>
    public IReadOnlyList<TraceSpan> Get(string? traceId)
    {
        if (traceId is null || !_traces.TryGetValue(traceId, out var bag) || bag.IsEmpty)
        {
            return [];
        }

        var spans = bag.OrderBy(s => s.Start).ThenByDescending(s => s.Duration).ToArray();
        var origin = spans[0].Start;
        return spans.Select(s => new TraceSpan(s.Kind, s.Label, (s.Start - origin).TotalMilliseconds, s.Duration.TotalMilliseconds, s.Error)).ToArray();
    }

    /// <summary>Request, agent, model, tool, person (confirmation wait) or data span; null for spans the timeline does not show.</summary>
    public static string? Classify(Activity a)
    {
        var name = a.DisplayName;
        if (a.Kind == ActivityKind.Server && name.Contains("/api/chat", StringComparison.Ordinal))
        {
            return "request";
        }

        if (name.StartsWith("invoke_agent", StringComparison.Ordinal))
        {
            return "agent";
        }

        if (name.StartsWith("chat ", StringComparison.Ordinal))
        {
            return "model";
        }

        if (name.StartsWith("execute_tool", StringComparison.Ordinal))
        {
            // log_decision waits for a person to confirm; show that time as the person's, not the engine's.
            return name.Contains("log_decision", StringComparison.Ordinal) ? "person" : "tool";
        }

        var host = a.GetTagItem("server.address") as string;
        return a.Kind == ActivityKind.Client && host is not null && host.Contains("documents.azure.com", StringComparison.OrdinalIgnoreCase)
            ? "data"
            : null;
    }

    private static string Label(Activity a, string kind) => kind switch
    {
        "request" => "POST /api/chat (studio)",
        "agent" => "Agent run",
        "model" => "Model call · " + (a.GetTagItem("gen_ai.request.model") as string ?? a.DisplayName[5..]),
        "tool" => (a.GetTagItem("gen_ai.tool.name") as string ?? a.DisplayName.Replace("execute_tool", string.Empty).Trim()) + " · cost engine",
        "person" => "log_decision · waiting for a person to confirm",
        "data" => "Cosmos DB · decisions",
        _ => a.DisplayName,
    };
}
