using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;

namespace CostToServe.Core.Tests;

public sealed class GuardrailAndValidationTests
{
    private static readonly CostEngine Engine = new(SeedDataSet.Load(TestPaths.SeedDirectory).WeeklyInputs);

    [Fact]
    public void Express_guardrail_passes_at_exactly_80_percent()
    {
        var check = CostEngine.CheckGuardrail(new Levers { Batch = 0.4, Basket = true });

        Assert.Equal(80, check.ExpressWithin3HoursPct);
        Assert.True(check.Passed);
    }

    [Fact]
    public void Express_guardrail_fails_below_80_percent()
    {
        var check = CostEngine.CheckGuardrail(new Levers { Batch = 0.45 });

        Assert.Equal(79.75, check.ExpressWithin3HoursPct);
        Assert.False(check.Passed);
        Assert.Contains(check.Findings, f => f is { Code: "EXPRESS_3H_BELOW_80", Severity: GuardrailSeverity.Blocking });
    }

    [Fact]
    public void Non_blocking_findings_do_not_fail_the_guardrail()
    {
        var check = CostEngine.CheckGuardrail(new Levers { Fee = 2, Closer = 0.35, Pickup = 0.08, Basket = true });

        Assert.True(check.Passed);
        Assert.Equal(
            ["EXPRESS_FEE_ABOVE_1_50", "CLOSER_STORE_STOCK_CHECK", "MIN_BASKET_RURAL_ONLY", "PICKUP_INCENTIVE_PAYBACK"],
            check.Findings.Select(f => f.Code));
    }

    public static TheoryData<Levers> OutOfRangeLevers => new()
    {
        new Levers { Batch = 0.61 },
        new Levers { Batch = -0.01 },
        new Levers { Closer = 0.41 },
        new Levers { Split = 0.61 },
        new Levers { Pickup = 0.11 },
        new Levers { Fee = -1.01 },
        new Levers { Fee = 3.01 },
        new Levers { Batch = double.NaN },
        new Levers { Fee = double.PositiveInfinity },
    };

    [Theory]
    [MemberData(nameof(OutOfRangeLevers))]
    public void Out_of_range_levers_are_rejected(Levers levers)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Engine.Scenario(levers, 0.5, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => CostEngine.CheckGuardrail(levers));
    }

    [Theory]
    [InlineData(-0.51, 8)]
    [InlineData(1.01, 8)]
    [InlineData(double.NaN, 8)]
    [InlineData(0.5, 0)]
    [InlineData(0.5, 14)]
    public void Out_of_range_shock_is_rejected(double shift, int duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Engine.Scenario(Levers.None, shift, duration));
    }

    [Fact]
    public void Lever_limits_are_inclusive()
    {
        var max = new Levers { Batch = 0.6, Closer = 0.4, Split = 0.6, Pickup = 0.1, Fee = 3, Basket = true, Cap = true };

        _ = Engine.Scenario(max, 1, 13);
        _ = Engine.Scenario(new Levers { Fee = -1 }, -0.5, 1);
    }

    [Fact]
    public void Regions_are_case_insensitive_and_validated()
    {
        var lower = Engine.Scenario(Levers.None, 0.5, 13, ["southeast"]);
        var canonical = Engine.Scenario(Levers.None, 0.5, 13, ["Southeast"]);

        Assert.Equal(canonical.FuelShockQuarterCost, lower.FuelShockQuarterCost);
        Assert.Equal(["Southeast"], lower.Regions);
        Assert.Throws<ArgumentException>(() => Engine.Scenario(Levers.None, 0.5, 13, ["Northeast"]));
        Assert.Throws<ArgumentException>(() => Engine.Scenario(Levers.None, 0.5, 13, []));
    }

    [Fact]
    public void Southeast_scenario_is_a_subset_of_the_network()
    {
        var southeast = Engine.Scenario(Levers.None, 0.5, 13, ["Southeast"]);
        var network = Engine.Scenario(Levers.None, 0.5, 13);

        Assert.InRange(southeast.FuelShockQuarterCost, 1, network.FuelShockQuarterCost - 1);
    }

    [Theory]
    [InlineData("Same-day", DeliveryType.SameDay)]
    [InlineData("same_day", DeliveryType.SameDay)]
    [InlineData("SameDay", DeliveryType.SameDay)]
    [InlineData("express", DeliveryType.Express)]
    public void Delivery_types_parse_seed_and_api_spellings(string value, DeliveryType expected)
    {
        Assert.Equal(expected, DeliveryTypes.Parse(value));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("Drone")]
    [InlineData("")]
    public void Unknown_delivery_types_are_rejected(string value)
    {
        Assert.Throws<ArgumentException>(() => DeliveryTypes.Parse(value));
    }
}
