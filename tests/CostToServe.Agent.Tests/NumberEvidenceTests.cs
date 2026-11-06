using System.Text.Json.Nodes;
using CostToServe.Agent.Evidence;

namespace CostToServe.Agent.Tests;

/// <summary>The live grounding check shown under every answer (and used by the evaluation).</summary>
public sealed class NumberEvidenceTests
{
    private static readonly (string, JsonNode?)[] Tools =
    [
        ("explain_variance", JsonNode.Parse("""{"changePct":0.067,"drivers":[{"name":"fuel","sharePct":61.2,"deltaUsd":0.31}]}""")),
    ];

    [Fact]
    public void Traces_each_number_to_its_tool_field_including_fractions_shown_as_percent()
    {
        var evidence = NumberEvidence.Trace("Cost per order rose 6.7%, with fuel at 61.2% of the change (+$0.31).", Tools);

        Assert.Equal(3, evidence.Quoted);
        Assert.Equal(3, evidence.Traced);
        Assert.Empty(evidence.Unsupported);
        Assert.Contains(evidence.Numbers, n => n.Text.StartsWith("6.7") && n.Source == "explain_variance.changePct");
        Assert.Contains(evidence.Numbers, n => n.Source == "explain_variance.drivers[0].sharePct");
    }

    [Fact]
    public void Flags_numbers_with_no_source_and_skips_small_counts()
    {
        var evidence = NumberEvidence.Trace("Across 3 regions the saving is $0.47 per order.", Tools);

        Assert.Equal(1, evidence.Quoted);
        Assert.Equal(["$0.47"], evidence.Unsupported);
    }

    [Fact]
    public void Numbers_from_the_question_or_instructions_count_as_sourced()
    {
        var evidence = NumberEvidence.Trace("At +$0.50 a gallon for 13 weeks, the 3-hour window holds.", [], "What if diesel rises $0.50?", "Express window is 3 hours.");

        Assert.Empty(evidence.Unsupported);
        Assert.Contains(evidence.Numbers, n => n.Source == "your question");
    }
}
