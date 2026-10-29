using CostToServe.Core.Model;

namespace CostToServe.Core.Engine;

/// <summary>Express cost per order in pilot zones (actions applied) versus control zones.</summary>
public sealed record PilotComparison(double PilotBefore, double PilotAfter, double ControlBefore, double ControlAfter)
{
    public double PilotChangePct => PilotBefore != 0 ? PilotAfter / PilotBefore - 1 : 0;

    public double ControlChangePct => ControlBefore != 0 ? ControlAfter / ControlBefore - 1 : 0;
}

/// <summary>
/// Modeled pilot readout after actions are executed, ported from the reference demo's
/// "Next day: the result" step: the first zones of the pilot region run the actions, the rest
/// are the control. Uses current-week zone volumes. Control zones are assumed flat apart from a
/// small +0.2% drift, as in the reference. Illustrative, not measured telemetry.
/// </summary>
public static class PilotModel
{
    public const string PilotRegion = "Southeast";
    public const int PilotZoneCount = 3;
    public const double ControlDrift = 0.002;

    public static double ExpressCostPerOrder(IEnumerable<TypeInputs> zones, Levers levers)
    {
        var m = levers.ToModifiers();
        double cost = 0, orders = 0;
        foreach (var z in zones)
        {
            cost += CostEngine.Unit(DeliveryType.Express, z, z.Diesel, m).Total * z.Orders;
            orders += z.Orders;
        }

        return orders > 0 ? cost / orders : 0;
    }

    public static PilotComparison Compare(IReadOnlyList<TypeInputs> pilotZones, IReadOnlyList<TypeInputs> controlZones, Levers levers)
    {
        var controlBefore = ExpressCostPerOrder(controlZones, Levers.None);
        return new PilotComparison(
            ExpressCostPerOrder(pilotZones, Levers.None),
            ExpressCostPerOrder(pilotZones, levers),
            controlBefore,
            controlBefore * (1 + ControlDrift));
    }
}
