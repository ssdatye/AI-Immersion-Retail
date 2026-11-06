using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CostToServe.Agent.Evidence;

namespace CostToServe.Evals;

/// <summary>
/// Deterministic checks that need no model: number extraction and matching, tool-call matching
/// and support for quoted numbers. Pure functions, unit tested.
/// </summary>
public static partial class EvalChecks
{
    /// <summary>Numbers in free text; shared with the live check (<see cref="NumberEvidence.ExtractNumbers"/>).</summary>
    public static IReadOnlyList<QuotedNumber> ExtractNumbers(string text) => NumberEvidence.ExtractNumbers(text);

    /// <summary>Matches at the precision written; shared with the live check (<see cref="NumberEvidence.Matches"/>).</summary>
    public static bool Matches(QuotedNumber quoted, double expected) => NumberEvidence.Matches(quoted, expected);

    public static QuotedNumber? FindMatch(IEnumerable<QuotedNumber> quoted, double expected) =>
        quoted.FirstOrDefault(q => Matches(q, expected));

    /// <summary>Every number inside a JSON value, including numbers written inside strings.</summary>
    public static IEnumerable<double> NumbersIn(JsonNode? node) => NumberEvidence.NumbersWithPaths(node).Select(n => n.Value);

    /// <summary>
    /// Quoted numbers that no source supports: not in the question, the tool results or the agent's
    /// instructions, and not a small count (0 to 13: weeks, zones, actions). These are numbers the model
    /// made up or calculated itself, which the design forbids.
    /// </summary>
    public static IReadOnlyList<QuotedNumber> Unsupported(IEnumerable<QuotedNumber> quoted, IReadOnlyCollection<double> sources)
    {
        var supported = sources.Concat(sources.Select(s => s * 100)).Concat(sources.Select(s => s / 100)).ToArray();
        return quoted
            .Where(q => !q.IsSmallCount)
            .Where(q => !supported.Any(s => Matches(q, s)))
            .ToArray();
    }

    /// <summary>
    /// Whether an actual tool call satisfies an expectation. Expected arguments are a subset: each
    /// listed argument must match (strings case-insensitively; numbers within 1e-6; action lists as sets).
    /// An array of values means any of them; JSON null means the argument may be omitted.
    /// </summary>
    public static bool CallMatches(string expectedName, JsonObject? expectedArgs, string actualName, JsonObject actualArgs)
    {
        if (!string.Equals(expectedName, actualName, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (key, expected) in expectedArgs ?? [])
        {
            actualArgs.TryGetPropertyValue(key, out var actual);
            var options = expected is JsonArray arr ? arr.ToArray() : [expected];
            if (!options.Any(o => ValueMatches(key, o, actual)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValueMatches(string key, JsonNode? expected, JsonNode? actual)
    {
        if (expected is null)
        {
            return actual is null || (actual is JsonValue v && v.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetValue<string>()));
        }

        if (actual is null)
        {
            return false;
        }

        if (expected.GetValueKind() == JsonValueKind.Number)
        {
            return actual.GetValueKind() == JsonValueKind.Number && Math.Abs(actual.GetValue<double>() - expected.GetValue<double>()) < 1e-6
                || actual.GetValueKind() == JsonValueKind.String && double.TryParse(actual.GetValue<string>(), CultureInfo.InvariantCulture, out var parsed)
                && Math.Abs(parsed - expected.GetValue<double>()) < 1e-6;
        }

        if (expected.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return actual.GetValueKind() == expected.GetValueKind();
        }

        var e = expected.ToString().Trim();
        var a = actual.GetValueKind() == JsonValueKind.String ? actual.GetValue<string>().Trim() : actual.ToJsonString();
        if (key == "action_ids")
        {
            static string[] Ids(string s) => [.. s.ToUpperInvariant().Split([',', ' ', '+', ';'], StringSplitOptions.RemoveEmptyEntries).Order()];
            return Ids(e).SequenceEqual(Ids(a));
        }

        return string.Equals(e, a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a value by a simple path such as "drivers[0].sharePct" or "expressCostPerOrderUsd.pilotAfter".</summary>
    public static JsonNode? Select(JsonNode? root, string path)
    {
        var node = root;
        foreach (Match part in PathPart().Matches(path))
        {
            if (node is null)
            {
                return null;
            }

            node = part.Groups["name"].Success
                ? (node as JsonObject)?[part.Groups["name"].Value]
                : (node as JsonArray) is { } arr && int.Parse(part.Groups["index"].Value, CultureInfo.InvariantCulture) is var i && i < arr.Count ? arr[i] : null;
        }

        return node;
    }

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)|\[(?<index>\d+)\]")]
    private static partial Regex PathPart();
}
