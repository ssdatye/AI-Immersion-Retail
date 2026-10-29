using CostToServe.Core.Model;

namespace CostToServe.Core.Data;

public sealed record ZoneRecord(string Region, string Zone, string Density, double MilesToStore);

public sealed record OrderDay(
    DateOnly Date, string Region, string Zone, DeliveryType DeliveryType,
    double Orders, double AvgBasketUsd, double SplitRate);

public sealed record RouteDay(
    DateOnly Date, string Region, string Zone, DeliveryType DeliveryType,
    double Routes, double Miles, double Stops, double OrdersPerRoute, double MilesPerOrder);

public sealed record FuelDay(DateOnly Date, string Region, double DieselUsdPerGallon, double GasolineUsdPerGallon);

public sealed record CostDay(
    DateOnly Date, string Region, string Zone, DeliveryType DeliveryType, double Orders,
    double FuelCost, double LaborCost, double CarrierCost, double VehicleCost, double CostPerOrder);

public sealed record FuelOutlookWeek(DateOnly WeekStart, string Region, double DieselLow, double DieselBase, double DieselHigh);

public sealed record MarketEvent(DateOnly Date, string EventType, string Region, string ExpectedDieselEffectUsdPerGallon, string Credibility);

public sealed record CarrierInvoiceWeek(
    DateOnly WeekStart, string Region, string Carrier, double BaseChargeUsd,
    double FuelSurchargePct, double ContractIndexSurchargePct, string Index);

public sealed record PlaybookAction(
    string ActionId, string Action, string Lever, string Effect, string CustomerImpact, string Approver, string Guardrail);

/// <summary>A complete 7-day demo week; weeks are numbered from 1 starting at the first seed date.</summary>
public sealed record DemoWeek(int Number, DateOnly Start, DateOnly End);
