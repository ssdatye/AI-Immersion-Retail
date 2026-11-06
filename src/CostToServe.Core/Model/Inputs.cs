namespace CostToServe.Core.Model;

/// <summary>
/// Weekly operating inputs for one region (or zone) and delivery type.
/// Route inputs are present only for Same-day and Express.
/// </summary>
public sealed record TypeInputs(
    double Orders,
    double SplitRate,
    double Diesel,
    double? OrdersPerRoute = null,
    double? MilesPerOrder = null);

/// <summary>Per-order cost components, in USD.</summary>
public sealed record UnitCost(double Fuel, double Labor, double Carrier, double Vehicle)
{
    // Summation order matches the reference engine's tot() for bit-level parity.
    public double Total => Fuel + Labor + Carrier + Vehicle;
}

/// <summary>Lever effects applied inside the per-order cost formulas.</summary>
public sealed record UnitModifiers(
    double MilesPerOrderMultiplier = 1,
    double ExpressOrdersPerRouteMultiplier = 1,
    double SplitRateMultiplier = 1,
    bool SurchargeCap = false)
{
    public static UnitModifiers None { get; } = new();
}
