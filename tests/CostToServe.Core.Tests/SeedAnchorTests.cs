using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;

namespace CostToServe.Core.Tests;

/// <summary>
/// Demo anchor numbers, computed in CSV mode from data/seed (the source of truth).
/// The CSV stores daily diesel to 3 decimals, so weekly diesel can differ from the
/// HTML's embedded value by up to 0.0006 $/gal; anchors are asserted at display precision.
/// </summary>
public sealed class SeedAnchorTests
{
    private static readonly SeedDataSet Seed = SeedDataSet.Load(TestPaths.SeedDirectory);
    private static readonly CostEngine Engine = new(Seed.WeeklyInputs);
    private static readonly Levers A1A2 = new() { Batch = 0.35, Closer = 0.2 };

    [Fact]
    public void Seed_covers_six_complete_weeks_ending_2026_10_25()
    {
        Assert.Equal(6, Seed.Weeks.Count);
        Assert.Equal(6, Seed.WeeklyInputs.CurrentWeek);
        Assert.Equal(5, Seed.WeeklyInputs.PriorWeek);
        Assert.Equal(new DateOnly(2026, 10, 19), Seed.Weeks[^1].Start);
        Assert.Equal(new DateOnly(2026, 10, 25), Seed.Weeks[^1].End);
        Assert.Equal(["Southeast", "Midwest", "West"], Seed.Regions);
    }

    [Fact]
    public void Southeast_express_cost_per_order_rises_from_15_56_to_17_01()
    {
        var v = Engine.ExplainVariance("Southeast", DeliveryType.Express);

        Assert.Equal(15.56, Math.Round(v.PriorCostPerOrder, 2));
        Assert.Equal(17.01, Math.Round(v.CurrentCostPerOrder, 2));
        Assert.Equal(9.3, Math.Round(v.ChangePct * 100, 1));
    }

    [Fact]
    public void Southeast_express_rise_splits_57_fuel_34_density_10_split()
    {
        var v = Engine.ExplainVariance("Southeast", DeliveryType.Express);

        Assert.Equal(57, Math.Round(v.FuelPriceShare!.Value * 100));
        Assert.Equal(34, Math.Round(v.RouteDensityShare!.Value * 100));
        Assert.Equal(10, Math.Round(v.SplitShipmentShare!.Value * 100));
    }

    [Fact]
    public void Diesel_up_50_cents_for_13_weeks_adds_about_28_2_million_network_wide()
    {
        var s = Engine.Scenario(Levers.None, 0.5, 13);

        Assert.Equal(28.2, Math.Round(s.FuelShockQuarterCost / 1e6, 1));
        Assert.Equal(3, s.Regions.Count);
    }

    [Fact]
    public void A1_plus_A2_offsets_about_88_percent_and_keeps_the_express_guardrail()
    {
        var s = Engine.Scenario(A1A2, 0.5, 13);
        var guardrail = CostEngine.CheckGuardrail(A1A2);

        Assert.Equal(88, Math.Round(s.OffsetShare!.Value * 100));
        Assert.Equal(84.25, s.ExpressWithin3HoursPct, 10);
        Assert.True(guardrail.Passed);
        Assert.Empty(guardrail.Findings);
    }

    [Fact]
    public void Network_base_week_is_about_66_million_and_9_1_million_orders()
    {
        var w = Engine.Weekly(Levers.None, 0);

        Assert.Equal(66, Math.Round(w.Cost / 1e6));
        Assert.Equal(9.1, Math.Round(w.Orders / 1e6, 1));
    }

    [Fact]
    public void Seed_weekly_inputs_reproduce_the_html_embedded_inputs()
    {
        var html = GoldenFixture.Load().Inputs;
        foreach (var week in new[] { 5, 6 })
        {
            foreach (var region in Seed.Regions)
            {
                foreach (var type in DeliveryTypes.All)
                {
                    var csv = Seed.WeeklyInputs.Get(week, region, type);
                    var embedded = html.Get(week, region, type);
                    var ctx = $"w{week} {region} {type}";

                    Assert.True(embedded.Orders == csv.Orders, ctx + " orders");
                    Assert.True(embedded.SplitRate == Math.Round(csv.SplitRate, 4), ctx + " split");
                    Assert.True(Math.Abs(embedded.Diesel - csv.Diesel) <= 0.0006, ctx + " diesel");
                    if (type.IsRouted())
                    {
                        Assert.True(embedded.OrdersPerRoute == Math.Round(csv.OrdersPerRoute!.Value, 3), ctx + " opr");
                        Assert.True(embedded.MilesPerOrder == Math.Round(csv.MilesPerOrder!.Value, 3), ctx + " mpo");
                    }
                }
            }
        }
    }

    [Fact]
    public void Cost_facts_agree_with_the_engine_to_the_cent()
    {
        var v = Engine.ExplainVariance("Southeast", DeliveryType.Express);
        var prior = Seed.QueryCostPerOrder(new CostQuery("Southeast", DeliveryType: DeliveryType.Express, From: Seed.Weeks[4].Start, To: Seed.Weeks[4].End));
        var current = Seed.QueryCostPerOrder(new CostQuery("Southeast", DeliveryType: DeliveryType.Express));

        Assert.Equal(Math.Round(v.PriorCostPerOrder, 2), Math.Round(prior.CostPerOrder, 2));
        Assert.Equal(Math.Round(v.CurrentCostPerOrder, 2), Math.Round(current.CostPerOrder, 2));
    }
}
