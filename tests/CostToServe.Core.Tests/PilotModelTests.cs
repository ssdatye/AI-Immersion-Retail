using System.Text.Json;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;

namespace CostToServe.Core.Tests;

public sealed class PilotModelTests
{
    private static readonly GoldenFixture Golden = GoldenFixture.Load();
    private static readonly SeedDataSet Seed = SeedDataSet.Load(TestPaths.SeedDirectory);

    private static (TypeInputs[] Pilot, TypeInputs[] Control) HtmlZones()
    {
        var zones = Golden.Root.GetProperty("zonesSE").EnumerateArray().Select(z =>
        {
            var w = z.GetProperty("w6");
            return new TypeInputs(w.GetProperty("orders").GetDouble(), w.GetProperty("split").GetDouble(), w.GetProperty("die").GetDouble(),
                w.GetProperty("opr").GetDouble(), w.GetProperty("mpo").GetDouble());
        }).ToArray();
        return (zones[..PilotModel.PilotZoneCount], zones[PilotModel.PilotZoneCount..]);
    }

    [Fact]
    public void Pilot_readout_matches_the_reference_html()
    {
        var (pilot, control) = HtmlZones();
        var count = 0;
        foreach (var c in Golden.Cases("pilotCases"))
        {
            var r = PilotModel.Compare(pilot, control, GoldenFixture.ReadLevers(c.GetProperty("levers")));
            var e = c.GetProperty("expected");
            Approx.Equal(e.GetProperty("pilotBefore").GetDouble(), r.PilotBefore, $"pilot[{count}] before");
            Approx.Equal(e.GetProperty("pilotAfter").GetDouble(), r.PilotAfter, $"pilot[{count}] after");
            Approx.Equal(e.GetProperty("controlBefore").GetDouble(), r.ControlBefore, $"pilot[{count}] control before");
            Approx.Equal(e.GetProperty("controlAfter").GetDouble(), r.ControlAfter, $"pilot[{count}] control after");
            count++;
        }

        Assert.Equal(4, count);
    }

    [Fact]
    public void Seed_zone_inputs_reproduce_the_html_embedded_zone_inputs()
    {
        foreach (var z in Golden.Root.GetProperty("zonesSE").EnumerateArray())
        {
            var zone = z.GetProperty("zone").GetString()!;
            foreach (var (week, key) in new[] { (5, "w5"), (6, "w6") })
            {
                var html = z.GetProperty(key);
                var csv = Seed.GetZoneInputs(week, zone, DeliveryType.Express);
                var ctx = $"{zone} w{week}";
                Assert.True(html.GetProperty("orders").GetDouble() == csv.Orders, ctx + " orders");
                Assert.True(html.GetProperty("split").GetDouble() == Math.Round(csv.SplitRate, 4), ctx + " split");
                Assert.True(html.GetProperty("opr").GetDouble() == Math.Round(csv.OrdersPerRoute!.Value, 3), ctx + " opr");
                Assert.True(html.GetProperty("mpo").GetDouble() == Math.Round(csv.MilesPerOrder!.Value, 3), ctx + " mpo");
                // The HTML rounds each zone's diesel separately (3.700 vs 3.701 within one region); allow one rounding step.
                Assert.True(Math.Abs(html.GetProperty("die").GetDouble() - csv.Diesel) <= 0.001, ctx + " diesel");
            }
        }
    }

    [Fact]
    public void Seed_pilot_for_A1_plus_A2_matches_the_reference_to_the_cent()
    {
        var zones = Seed.ZonesIn(PilotModel.PilotRegion).Select(z => Seed.GetZoneInputs(Seed.WeeklyInputs.CurrentWeek, z.Zone, DeliveryType.Express)).ToArray();
        var r = PilotModel.Compare(zones[..PilotModel.PilotZoneCount], zones[PilotModel.PilotZoneCount..], new Levers { Batch = 0.35, Closer = 0.2 });
        var expected = Golden.Cases("pilotCases").ElementAt(2).GetProperty("expected");

        Assert.Equal(Math.Round(expected.GetProperty("pilotBefore").GetDouble(), 2), Math.Round(r.PilotBefore, 2));
        Assert.Equal(Math.Round(expected.GetProperty("pilotAfter").GetDouble(), 2), Math.Round(r.PilotAfter, 2));
        Assert.Equal(Math.Round(expected.GetProperty("controlBefore").GetDouble(), 2), Math.Round(r.ControlBefore, 2));
        Assert.True(r.PilotChangePct < -0.08);
        Assert.Equal(0.002, r.ControlChangePct, 10);
    }

    [Fact]
    public void Southeast_has_three_pilot_and_four_control_zones()
    {
        var zones = Seed.ZonesIn("southeast");

        Assert.Equal(7, zones.Count);
        Assert.Equal(["Urban", "Urban", "Suburban"], zones.Take(3).Select(z => z.Density));
    }
}
