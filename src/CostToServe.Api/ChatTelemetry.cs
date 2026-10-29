using System.Collections.Concurrent;
using System.Diagnostics;
using CostToServe.Agent.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CostToServe.Api;

internal static class ChatTelemetry
{
    public const string ChatSourceName = "CostToServe.Chat";
    public static readonly ActivitySource Source = new(ChatSourceName);

    /// <summary>
    /// Traces agent, model, tool and HTTP spans. Collects one-line summaries in
    /// <paramref name="summaries"/> when set, and exports via OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set.
    /// </summary>
    public static TracerProvider Create(SpanSummaries? summaries)
    {
        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("cost-to-serve-chat"))
            .AddSource(ChatSourceName)
            .AddSource([.. AgentTelemetry.AllSources])
            .AddHttpClientInstrumentation();

        if (summaries is not null)
        {
            builder.AddProcessor(summaries);
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
        {
            builder.AddOtlpExporter();
        }

        return builder.Build();
    }
}

/// <summary>Buffers metadata-only span summaries (no prompts, responses or tool payloads) for printing after each turn.</summary>
internal sealed class SpanSummaries : BaseProcessor<Activity>
{
    private static readonly string[] Tags =
    [
        "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens", AgentTelemetry.ToolOutcomeTag,
        AgentTelemetry.ScopeTag, AgentTelemetry.DecisionTag, "http.response.status_code",
    ];

    private readonly ConcurrentQueue<string> _lines = new();

    public override void OnEnd(Activity activity)
    {
        if (activity.Source.Name == ChatTelemetry.ChatSourceName)
        {
            return;
        }

        var details = Tags
            .Select(t => (Key: t, Value: activity.GetTagItem(t)))
            .Where(t => t.Value is not null)
            .Select(t => $"{t.Key.Split('.')[^1]}={t.Value}");
        var name = activity.GetTagItem("http.request.method") is not null && activity.GetTagItem("server.address") is { } host
            ? $"{activity.DisplayName} {host}"
            : activity.DisplayName;
        var status = activity.Status == ActivityStatusCode.Error ? " ERROR" : string.Empty;
        _lines.Enqueue($"{name} {activity.Duration.TotalMilliseconds:F0}ms{status} {string.Join(' ', details)}".TrimEnd());
    }

    public void Flush(TextWriter writer, ActivityTraceId traceId)
    {
        writer.WriteLine($"\u001b[2m  trace {traceId.ToHexString()} (spans in end order):");
        while (_lines.TryDequeue(out var line))
        {
            writer.WriteLine($"    {line}");
        }

        writer.Write("\u001b[0m");
    }
}
