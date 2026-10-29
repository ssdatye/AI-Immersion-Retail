using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CostToServe.Agent.Evidence;

/// <summary>A number quoted in an answer, with the precision and scale it was written in.</summary>
public sealed record QuotedNumber(string Text, double Display, int Decimals, double Scale)
{
    public double Value => Display * Scale;

    /// <summary>Small whole numbers (weeks, zones, action counts) that need no source.</summary>
    public bool IsSmallCount => Scale == 1 && Decimals == 0 && Math.Abs(Display) <= 13;
}

/// <summary>Where one quoted number came from, or null when no source contains it.</summary>
public sealed record TracedNumber(string Text, string? Source);

/// <summary>The evidence for one answer: every quoted number and its source.</summary>
public sealed record AnswerEvidence(IReadOnlyList<TracedNumber> Numbers)
{
    public int Quoted => Numbers.Count;

    public int Traced => Numbers.Count(n => n.Source is not null);

    public IReadOnlyList<string> Unsupported => Numbers.Where(n => n.Source is null).Select(n => n.Text).ToArray();
}

/// <summary>
/// Checks that the numbers in an answer come from tool results, the question or the agent's
/// instructions, never from the model's own arithmetic. Pure functions: used live after every chat
/// answer and by the offline evaluation.
/// </summary>
public static partial class NumberEvidence
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

    /// <summary>Every number in a JSON value with its path, including numbers written inside strings.</summary>
    public static IEnumerable<(string Path, double Value)> NumbersWithPaths(JsonNode? node, string path = "")
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    foreach (var n in NumbersWithPaths(value, path.Length == 0 ? key : $"{path}.{key}"))
                    {
                        yield return n;
                    }
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    foreach (var n in NumbersWithPaths(a[i], $"{path}[{i}]"))
                    {
                        yield return n;
                    }
                }

                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                yield return (path, v.GetValue<double>());
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                foreach (var q in ExtractNumbers(v.GetValue<string>()))
                {
                    yield return (path, q.Value);
                }

                break;
        }
    }

    /// <summary>
    /// Traces every number in <paramref name="answer"/> to a source: a tool result field (for example
    /// "explain_variance.changePct"), the question, or the agent's instructions. Percentages may be
    /// stored as fractions or as percent values. Small counts need no source.
    /// </summary>
    public static AnswerEvidence Trace(
        string answer, IEnumerable<(string Tool, JsonNode? Result)> toolResults, string? question = null, string? instructions = null)
    {
        var sources = new List<(string Label, double Value)>();
        foreach (var (tool, result) in toolResults)
        {
            sources.AddRange(NumbersWithPaths(result).Select(n => ($"{tool}.{n.Path}", n.Value)));
        }

        if (question is not null)
        {
            sources.AddRange(ExtractNumbers(question).Select(q => ("your question", q.Value)));
        }

        if (instructions is not null)
        {
            sources.AddRange(ExtractNumbers(instructions).Select(q => ("agent instructions", q.Value)));
        }

        var numbers = new List<TracedNumber>();
        foreach (var q in ExtractNumbers(answer))
        {
            if (q.IsSmallCount)
            {
                continue;
            }

            var source = sources.FirstOrDefault(s => Matches(q, s.Value) || Matches(q, s.Value * 100) || Matches(q, s.Value / 100)).Label;
            numbers.Add(new TracedNumber(q.Text, source));
        }

        return new AnswerEvidence(numbers);
    }

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}(T[\d:.]+Z?)?\b|\b\d{1,2}:\d{2}(\s?[AaPp][Mm])?\b")]
    private static partial Regex DateOrTime();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_.\-])(?<sign>[-−])?\$?(?<int>\d{1,3}(?:,\d{3})+|\d+)(?<dec>\.\d+)?(?:\s?(?<suf>%|percent\b|million\b|mn\b|thousand\b|billion\b|bn\b|M\b|K\b|k\b|B\b))?(?![\p{L}\p{N}])")]
    private static partial Regex Number();
}
