using CostToServe.Core.Engine;
using CostToServe.Core.Model;

namespace CostToServe.Core.Tests;

/// <summary>
/// The C# engine, fed the reference HTML's embedded inputs, must reproduce the
/// original JavaScript engine's outputs for every golden case.
/// </summary>
public sealed class HtmlEngineParityTests
{
    private static readonly GoldenFixture Golden = GoldenFixture.Load();
    private static readonly CostEngine Engine = new(Golden.Inputs);

    [Fact]
    public void Golden_fixture_was_generated_from_the_committed_reference_html()
    {
        Assert.Equal(Golden.SourceSha256, TestPaths.Sha256(TestPaths.ReferenceHtml));
    }

    [Fact]
    public void Unit_costs_match_reference()
    {
        var count = 0;
        foreach (var c in Golden.Cases("unitCases"))
        {
            var type = DeliveryTypes.Parse(c.GetProperty("type").GetString()!);
            var week = c.GetProperty("week").GetInt32();
            var region = c.GetProperty("region").GetString()!;
            var levers = GoldenFixture.ReadLevers(c.GetProperty("levers"));
            var u = CostEngine.Unit(type, Golden.Inputs.Get(week, region, type), c.GetProperty("diesel").GetDouble(), levers.ToModifiers());
            var e = c.GetProperty("expected");
            var ctx = $"unit[{count}] w{week} {region} {type}";
            Approx.Equal(e.GetProperty("fuel").GetDouble(), u.Fuel, ctx + " fuel");
            Approx.Equal(e.GetProperty("labor").GetDouble(), u.Labor, ctx + " labor");
            Approx.Equal(e.GetProperty("carrier").GetDouble(), u.Carrier, ctx + " carrier");
            Approx.Equal(e.GetProperty("vehicle").GetDouble(), u.Vehicle, ctx + " vehicle");
            Approx.Equal(e.GetProperty("total").GetDouble(), u.Total, ctx + " total");
            count++;
        }

        Assert.Equal(96, count);
    }

    [Fact]
    public void Weekly_totals_match_reference()
    {
        var count = 0;
        foreach (var c in Golden.Cases("weeklyCases"))
        {
            var w = Engine.Weekly(
                GoldenFixture.ReadLevers(c.GetProperty("levers")),
                c.GetProperty("shift").GetDouble(),
                GoldenFixture.ReadRegions(c.GetProperty("regions")));
            var e = c.GetProperty("expected");
            var ctx = $"weekly[{count}]";
            Approx.Equal(e.GetProperty("cost").GetDouble(), w.Cost, ctx + " cost");
            Approx.Equal(e.GetProperty("fuel").GetDouble(), w.FuelCost, ctx + " fuel");
            Approx.Equal(e.GetProperty("orders").GetDouble(), w.Orders, ctx + " orders");
            Approx.Equal(e.GetProperty("fee").GetDouble(), w.ExpressFeeRevenue, ctx + " fee");
            Approx.Equal(e.GetProperty("incentive").GetDouble(), w.PickupIncentiveCost, ctx + " incentive");
            count++;
        }

        Assert.Equal(180, count);
    }

    [Fact]
    public void Scenarios_match_reference()
    {
        var count = 0;
        foreach (var c in Golden.Cases("scenarioCases"))
        {
            var s = Engine.Scenario(
                GoldenFixture.ReadLevers(c.GetProperty("levers")),
                c.GetProperty("shift").GetDouble(),
                c.GetProperty("duration").GetInt32());
            var e = c.GetProperty("expected");
            var ctx = $"scenario[{count}]";
            Approx.Equal(e.GetProperty("baseCost").GetDouble(), s.BaselineWeeklyCost, ctx + " base");
            Approx.Equal(e.GetProperty("shockCost").GetDouble(), s.ShockWeeklyCost, ctx + " shock");
            Approx.Equal(e.GetProperty("bothCost").GetDouble(), s.CombinedWeeklyCost, ctx + " both");
            Approx.Equal(e.GetProperty("shockQuarter").GetDouble(), s.FuelShockQuarterCost, ctx + " shockQ");
            Approx.Equal(e.GetProperty("scenarioQuarter").GetDouble(), s.ScenarioQuarterCost, ctx + " scenQ");
            Approx.Equal(e.GetProperty("netOperatingIncome").GetDouble(), s.NetOperatingIncomeImpact, ctx + " net");
            Approx.Equal(e.GetProperty("expressWithin3HoursPct").GetDouble(), s.ExpressWithin3HoursPct, ctx + " under3");
            Approx.Equal(e.GetProperty("promisePct").GetDouble(), s.PromiseKeptPct, ctx + " promise");
            Approx.Equal(e.GetProperty("availabilityPct").GetDouble(), s.AvailabilityPct, ctx + " avail");

            var offset = e.GetProperty("offset");
            if (offset.ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                Assert.Null(s.OffsetShare);
            }
            else
            {
                Assert.NotNull(s.OffsetShare);
                Approx.Equal(offset.GetDouble(), s.OffsetShare.Value, ctx + " offset");
            }

            count++;
        }

        Assert.Equal(160, count);
    }

    [Fact]
    public void Guardrail_messages_match_reference()
    {
        var count = 0;
        foreach (var c in Golden.Cases("guardCases"))
        {
            var expected = c.GetProperty("expected").EnumerateArray().Select(m => m.GetString()).ToArray();
            var actual = CostEngine.Guard(GoldenFixture.ReadLevers(c.GetProperty("levers"))).Select(f => f.Message).ToArray();
            Assert.Equal(expected, actual);
            count++;
        }

        Assert.Equal(20, count);
    }

    [Fact]
    public void Variance_steps_match_reference()
    {
        var count = 0;
        foreach (var c in Golden.Cases("varianceCases"))
        {
            var region = c.GetProperty("region").GetString()!;
            var type = DeliveryTypes.Parse(c.GetProperty("type").GetString()!);
            var v = Engine.ExplainVariance(region, type);
            var e = c.GetProperty("expected");
            var ctx = $"variance {region} {type}";
            Approx.Equal(e.GetProperty("prior").GetDouble(), v.PriorCostPerOrder, ctx + " prior");
            Approx.Equal(e.GetProperty("afterFuel").GetDouble(), v.AfterFuelPrice, ctx + " afterFuel");
            Approx.Equal(e.GetProperty("afterDensity").GetDouble(), v.AfterRouteDensity, ctx + " afterDensity");
            Approx.Equal(e.GetProperty("current").GetDouble(), v.CurrentCostPerOrder, ctx + " current");
            count++;
        }

        Assert.Equal(12, count);
    }

    [Fact]
    public void Action_savings_match_reference()
    {
        var count = 0;
        foreach (var c in Golden.Cases("actionCases"))
        {
            var saving = Engine.WeeklySaving(GoldenFixture.ReadLevers(c.GetProperty("levers")), [c.GetProperty("region").GetString()!]);
            Approx.Equal(c.GetProperty("expectedWeeklySaving").GetDouble(), saving, $"action {c.GetProperty("id").GetString()}");
            count++;
        }

        Assert.Equal(4, count);
    }
}
