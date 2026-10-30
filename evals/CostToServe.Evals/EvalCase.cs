using System.Text.Json;
using System.Text.Json.Nodes;
using CostToServe.Core.Model;

namespace CostToServe.Evals;

/// <summary>
/// A decision logged before the question is asked, to put the demo in a known state. With decision "package",
/// the persona creates and proposes a custom package from <see cref="Name"/> and <see cref="Levers"/> instead.
/// </summary>
public sealed record SetupDecision(
    string Persona, string Action, string Decision, string? Region, double? Setting, string? Reason, string? Name = null, Levers? Levers = null);

/// <summary>A tool expectation: one call, or any one of several calls.</summary>
public sealed record ToolExpectation(string? Name, JsonObject? Args, IReadOnlyList<ToolExpectation>? AnyOf)
{
    public string Describe() => AnyOf is { Count: > 0 }
        ? string.Join(" or ", AnyOf.Select(a => a.Describe()))
        : $"{Name}({string.Join(", ", (Args ?? []).Select(kv => $"{kv.Key}={kv.Value?.ToJsonString()}"))})";
}

/// <summary>Numbers the answer must (or must not) contain, read from a tool result computed by the engine.</summary>
public sealed record NumberSource(string Tool, JsonObject Args, string? As, IReadOnlyList<string> Fields);

/// <summary>The decision a case expects; <see cref="Levers"/> also requires a custom package with exactly those levers.</summary>
public sealed record DecisionExpectation(bool Recorded, string? Action, string? Decision, string? Region, double? Setting, Levers? Levers = null);

/// <summary>One evaluation case from evals/dataset.jsonl.</summary>
public sealed record EvalCase(
    string Id,
    string Persona,
    string Category,
    string Query,
    IReadOnlyList<SetupDecision> Setup,
    bool Confirm,
    IReadOnlyList<ToolExpectation> ExpectedTools,
    IReadOnlyList<string> ForbidTools,
    IReadOnlyList<NumberSource> ExpectedNumbers,
    IReadOnlyList<NumberSource> AbsentNumbers,
    DecisionExpectation? ExpectedDecision,
    string? Notes)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<EvalCase> Load(string path)
    {
        var cases = new List<EvalCase>();
        var line = 0;
        foreach (var raw in File.ReadLines(path))
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                cases.Add(Parse(JsonNode.Parse(raw)!.AsObject()));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} line {line}: {ex.Message}", ex);
            }
        }

        var duplicate = cases.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? cases : throw new InvalidDataException($"Duplicate case id '{duplicate.Key}'.");
    }

    private static EvalCase Parse(JsonObject o) => new(
        Required(o, "id"),
        Required(o, "persona"),
        (string?)o["category"] ?? "general",
        Required(o, "query"),
        (o["setup"] as JsonArray ?? []).Select(s => s.Deserialize<SetupDecision>(Json)!).ToArray(),
        (bool?)o["confirm"] ?? false,
        (o["expected_tools"] as JsonArray ?? []).Select(ParseTool).ToArray(),
        (o["forbid_tools"] as JsonArray ?? []).Select(t => (string)t!).ToArray(),
        (o["expected_numbers"] as JsonArray ?? []).Select(ParseSource).ToArray(),
        (o["absent_numbers"] as JsonArray ?? []).Select(ParseSource).ToArray(),
        o["expected_decision"] is JsonObject d
            ? new DecisionExpectation(
                (bool?)d["recorded"] ?? false, (string?)d["action"], (string?)d["decision"], (string?)d["region"], (double?)d["setting"],
                d["levers"]?.Deserialize<Levers>(Json))
            : null,
        (string?)o["notes"]);

    private static ToolExpectation ParseTool(JsonNode? n)
    {
        var o = n!.AsObject();
        return o["any_of"] is JsonArray any
            ? new ToolExpectation(null, null, any.Select(ParseTool).ToArray())
            : new ToolExpectation(Required(o, "name"), o["args"]?.DeepClone().AsObject(), null);
    }

    private static NumberSource ParseSource(JsonNode? n)
    {
        var o = n!.AsObject();
        return new NumberSource(
            Required(o, "tool"),
            o["args"]?.DeepClone().AsObject() ?? [],
            (string?)o["as"],
            (o["fields"] as JsonArray ?? throw new KeyNotFoundException("'fields' is required")).Select(f => (string)f!).ToArray());
    }

    private static string Required(JsonObject o, string key) =>
        (string?)o[key] is { Length: > 0 } v ? v : throw new KeyNotFoundException($"'{key}' is required");
}
