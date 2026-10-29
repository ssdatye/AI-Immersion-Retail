namespace CostToServe.Core.Model;

public sealed record TypeTotals(double Cost, double Orders)
{
    public double CostPerOrder => Orders > 0 ? Cost / Orders : 0;
}

/// <summary>One week of delivery cost at current-week volumes.</summary>
public sealed record WeeklyResult(
    double Cost,
    double FuelCost,
    double Orders,
    double ExpressFeeRevenue,
    double PickupIncentiveCost,
    IReadOnlyDictionary<DeliveryType, TypeTotals> ByType)
{
    public double CostPerOrder => Orders > 0 ? Cost / Orders : 0;
}

/// <summary>
/// Quarter (13-week) view of a diesel shock with levers. Positive costs are
/// increases versus the no-shock, no-lever baseline week.
/// </summary>
public sealed record ScenarioResult(
    double DieselShiftUsdPerGallon,
    int DurationWeeks,
    Levers Levers,
    IReadOnlyList<string> Regions,
    double BaselineWeeklyCost,
    double ShockWeeklyCost,
    double LeversWeeklyCost,
    double CombinedWeeklyCost,
    double FuelShockQuarterCost,
    double ScenarioQuarterCost,
    double NetOperatingIncomeImpact,
    double? OffsetShare,
    double ExpressWithin3HoursPct,
    double PromiseKeptPct,
    double AvailabilityPct);

public enum GuardrailSeverity
{
    Blocking,
    ApprovalRequired,
    Warning,
    Constraint,
}

public sealed record GuardrailFinding(string Code, GuardrailSeverity Severity, string Message);

public sealed record GuardrailCheck(
    double ExpressWithin3HoursPct,
    double ThresholdPct,
    bool Passed,
    IReadOnlyList<GuardrailFinding> Findings);

/// <summary>
/// Week-over-week cost-per-order change, attributed sequentially to fuel price,
/// route density (orders per route and miles per order), then split shipments.
/// </summary>
public sealed record VarianceResult(
    string Region,
    DeliveryType DeliveryType,
    int PriorWeek,
    int CurrentWeek,
    double PriorCostPerOrder,
    double AfterFuelPrice,
    double AfterRouteDensity,
    double CurrentCostPerOrder,
    TypeInputs PriorInputs,
    TypeInputs CurrentInputs)
{
    public double Change => CurrentCostPerOrder - PriorCostPerOrder;
    public double ChangePct => PriorCostPerOrder != 0 ? CurrentCostPerOrder / PriorCostPerOrder - 1 : 0;
    public double FuelPriceEffect => AfterFuelPrice - PriorCostPerOrder;
    public double RouteDensityEffect => AfterRouteDensity - AfterFuelPrice;
    public double SplitShipmentEffect => CurrentCostPerOrder - AfterRouteDensity;
    public double? FuelPriceShare => Share(FuelPriceEffect);
    public double? RouteDensityShare => Share(RouteDensityEffect);
    public double? SplitShipmentShare => Share(SplitShipmentEffect);

    private double? Share(double effect) => Change != 0 ? effect / Change : null;
}
