using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CostToServe.Evals;

/// <summary>A number quoted in an answer, with the precision and scale it was written in.</summary>
public sealed record QuotedNumber(string Text, double Display, int Decimals, double Scale)
{
    public double Value => Display * Scale;
}

/// <summary>
/// Deterministic checks that need no model: number extraction and matching, tool-call matching
/// and support for quoted numbers. Pure functions, unit tested.
/// </summary>
public static partial class EvalChecks
{
    /// <summary>
    /// Numbers in free text, such as "$15.56", "9.3%", "$28.2M", "670.6K", "1,234". IDs such as A1,
    /// FY27 or gpt-4.1 are skipped, and so are ISO dates and clock times.
    /// </summary>
    public static IReadOnlyList<QuotedNumber> ExtractNumbers(string text)
    {
        var cleaned = DateOrTime().Replace(text, " ");
        var result = new List<QuotedNumber>();
        foreach (Match m in Number().Matches(cleaned))
        {
            var digits = m.Groups["int"].Value.Replace(",", string.Empty) + m.Groups["dec"].Value;
            if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var display))
            {
                continue;
            }

            if (m.Groups["sign"].Success)
            {
                display = -display;
            }

            var decimals = m.Groups["dec"].Success ? m.Groups["dec"].Value.Length - 1 : 0;
            var scale = m.Groups["suf"].Value.ToLowerInvariant() switch
            {
                "k" or "thousand" => 1e3,
                "m" or "mn" or "million" => 1e6,
                "b" or "bn" or "billion" => 1e9,
                _ => 1,
            };
            result.Add(new QuotedNumber(m.Value.Trim(), display, decimals, scale));
        }

        return result;
    }

    /// <summary>
    /// True when a quoted number is the expected value at the precision it was written in, within one
    /// unit of the last shown digit (so 28,186,400 matches "$28.2M" and 9.31 matches "9.3%"). Signs are
    /// ignored because answers often say "a $17.4M hit" for -17.4M.
    /// </summary>
    public static bool Matches(QuotedNumber quoted, double expected)
    {
        var shown = Math.Abs(quoted.Display);
        var target = Math.Abs(expected) / quoted.Scale;
        var unit = Math.Pow(10, -quoted.Decimals);
        return Math.Abs(Math.Round(target, quoted.Decimals, MidpointRounding.AwayFromZero) - shown) <= unit + 1e-9;
    }

    public static QuotedNumber? FindMatch(IEnumerable<QuotedNumber> quoted, double expected) =>
        quoted.FirstOrDefault(q => Matches(q, expected));

    /// <summary>
    /// Every number inside a JSON value, including numbers written inside strings (for example
    /// "2026-10-19 to 2026-10-25" or "+0.10 to +0.30").
    /// </summary>
    public static IEnumerable<double> NumbersIn(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, v) in o)
                {
                    foreach (var n in NumbersIn(v))
                    {
                        yield return n;
                    }
                }

                break;
            case JsonArray a:
                foreach (var v in a)
                {
                    foreach (var n in NumbersIn(v))
                    {
                        yield return n;
                    }
                }

                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                yield return v.GetValue<double>();
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                foreach (var q in ExtractNumbers(v.GetValue<string>()))
                {
                    yield return q.Value;
                }

                break;
        }
    }

    /// <summary>
    /// Quoted numbers that no source supports: not in the question, the tool results or the agent's
    /// instructions, and not a small count (0 to 13: weeks, zones, actions). These are numbers the model
    /// made up or calculated itself, which the design forbids.
    /// </summary>
    public static IReadOnlyList<QuotedNumber> Unsupported(IEnumerable<QuotedNumber> quoted, IReadOnlyCollection<double> sources)
    {
        var supported = sources.Concat(sources.Select(s => s * 100)).Concat(sources.Select(s => s / 100)).ToArray();
        return quoted
            .Where(q => !(q.Scale == 1 && q.Decimals == 0 && Math.Abs(q.Display) <= 13))
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

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}(T[\d:.]+Z?)?\b|\b\d{1,2}:\d{2}(\s?[AaPp][Mm])?\b")]
    private static partial Regex DateOrTime();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_.\-])(?<sign>[-−])?\$?(?<int>\d{1,3}(?:,\d{3})+|\d+)(?<dec>\.\d+)?(?:\s?(?<suf>%|percent\b|million\b|mn\b|thousand\b|billion\b|bn\b|M\b|K\b|k\b|B\b))?(?![\p{L}\p{N}])")]
    private static partial Regex Number();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)|\[(?<index>\d+)\]")]
    private static partial Regex PathPart();
}
