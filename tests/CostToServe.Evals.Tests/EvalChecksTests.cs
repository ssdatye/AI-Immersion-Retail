using System.Text.Json.Nodes;

namespace CostToServe.Evals.Tests;

public sealed class EvalChecksTests
{
    [Fact]
    public void Extracts_money_percent_and_scaled_numbers_but_not_ids_dates_or_versions()
    {
        var text = "Southeast Express rose from $15.56 to $17.01 (+9.3%). A1 plus A2 offset 88% of the $28.2M shock; "
                 + "weekly saving $670.6K. Week 2026-10-19 to 2026-10-25, 9:08 AM, FY27, gpt-4.1-mini, -$17.4 million.";

        var numbers = EvalChecks.ExtractNumbers(text).Select(n => n.Value).ToArray();

        Assert.Equal([15.56, 17.01, 9.3, 88, 28.2e6, 670.6e3, -17.4e6], numbers, new Tolerance());
    }

    [Theory]
    [InlineData("$28.2M", 28_186_400, true)]
    [InlineData("$28.2M", 28_301_000, true)]
    [InlineData("$28.2M", 28_400_000, false)]
    [InlineData("9.3%", 9.31, true)]
    [InlineData("9%", 9.3, true)]
    [InlineData("$15.56", 15.56, true)]
    [InlineData("$15.56", 15.6, false)]
    [InlineData("88.1%", 88.1, true)]
    [InlineData("1,234,567", 1_234_567, true)]
    [InlineData("-$17.4 million", -17_412_000, true)]
    [InlineData("$17.4 million", -17_412_000, true)]
    public void Matches_at_the_precision_shown(string quoted, double expected, bool match)
    {
        Assert.Equal(match, EvalChecks.Matches(Assert.Single(EvalChecks.ExtractNumbers(quoted)), expected));
    }

    [Fact]
    public void Unsupported_numbers_are_those_no_source_contains()
    {
        var quoted = EvalChecks.ExtractNumbers("Cost rose to $17.01, a 9.3% change over 13 weeks, saving about $1.2M (my estimate: 42%).");
        var sources = new[] { 17.01, 9.3, 1_204_000 };

        Assert.Equal(["42%"], EvalChecks.Unsupported(quoted, sources).Select(u => u.Text));
    }

    [Fact]
    public void Numbers_inside_tool_results_include_strings()
    {
        var result = JsonNode.Parse("""{"a":1.5,"b":{"c":[2,3]},"week":"2026-10-19 to 2026-10-25","effect":"+0.10 to +0.30"}""");

        Assert.Equal([1.5, 2, 3, 0.10, 0.30], EvalChecks.NumbersIn(result).ToArray(), new Tolerance());
    }

    [Fact]
    public void Tool_calls_match_on_name_and_a_subset_of_arguments()
    {
        var actual = JsonNode.Parse("""{"region":"southeast","delivery_type":"Express","diesel_change_usd_per_gallon":0.5,"action_ids":"A2, a1"}""")!.AsObject();

        Assert.True(EvalChecks.CallMatches("x", JsonNode.Parse("""{"region":"Southeast"}""")!.AsObject(), "x", actual));
        Assert.True(EvalChecks.CallMatches("x", JsonNode.Parse("""{"action_ids":"A1,A2","diesel_change_usd_per_gallon":0.50}""")!.AsObject(), "x", actual));
        Assert.True(EvalChecks.CallMatches("x", JsonNode.Parse("""{"zone":null}""")!.AsObject(), "x", actual));
        Assert.True(EvalChecks.CallMatches("x", JsonNode.Parse("""{"region":["West","Southeast"]}""")!.AsObject(), "x", actual));
        Assert.False(EvalChecks.CallMatches("x", JsonNode.Parse("""{"region":"West"}""")!.AsObject(), "x", actual));
        Assert.False(EvalChecks.CallMatches("y", null, "x", actual));
        Assert.False(EvalChecks.CallMatches("x", JsonNode.Parse("""{"duration_weeks":13}""")!.AsObject(), "x", actual));
    }

    [Fact]
    public void Paths_select_nested_fields_and_array_items()
    {
        var node = JsonNode.Parse("""{"drivers":[{"sharePct":56.8}],"cpo":{"pilotAfter":12.66}}""");

        Assert.Equal(56.8, EvalChecks.Select(node, "drivers[0].sharePct")!.GetValue<double>());
        Assert.Equal(12.66, EvalChecks.Select(node, "cpo.pilotAfter")!.GetValue<double>());
        Assert.Null(EvalChecks.Select(node, "drivers[3].sharePct"));
    }

    [Fact]
    public void Dataset_is_valid_and_ids_are_unique()
    {
        var path = Path.Combine(RepoRoot(), "evals", "dataset.jsonl");

        var cases = EvalCase.Load(path);

        Assert.InRange(cases.Count, 20, 30);
        Assert.Contains(cases, c => c.Persona == "dana");
        Assert.Contains(cases, c => c.Persona == "luis");
        Assert.All(cases, c => Assert.True(c.ExpectedTools.Count > 0 || c.ForbidTools.Count > 0 || c.ExpectedDecision is not null || c.Notes is not null, c.Id));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CostToServe.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class Tolerance : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-6 * Math.Max(1, Math.Abs(x));

        public int GetHashCode(double obj) => 0;
    }
}
