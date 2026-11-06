using CostToServe.Core.Model;

namespace CostToServe.Core.Engine;

/// <summary>
/// Deterministic cost-to-serve engine, ported from the reference demo
/// (docs/reference/walmart-v3-cost-to-serve-demo.html: unit, weekly, scenario,
/// guard). Arithmetic order deliberately mirrors the reference so results match
/// to floating-point precision; see the golden parity tests.
/// All data is illustrative.
/// </summary>
public sealed class CostEngine(IWeeklyInputs inputs)
{
    public const int QuarterWeeks = 13;
    public const double ExpressWithin3HoursThresholdPct = 80;

    // Reference constants: C={MPG,K,WAGE,VEH,HRS,PL,PO,CB,OVER}.
    public const double MilesPerGallon = 12;
    public const double FuelLinkedMileagePay = 0.16;
    public const double HourlyWage = 28;
    public const double VehicleCostPerMile = 0.55;
    public const double SameDayRouteHours = 3.0;
    public const double ExpressRouteHours = 1.15;
    public const double PickupLabor = 2.55;
    public const double PickupOverhead = 0.55;
    public const double CarrierBaseCharge = 5.45;
    public const double CarrierOverIndexSurcharge = 0.004;
    public const double PickupIncentivePerOrder = 0.75;

    public IWeeklyInputs Inputs => inputs;

    /// <summary>Carrier fuel surcharge rate for a diesel price (reference surch()).</summary>
    public static double CarrierSurcharge(double diesel) => 0.10 + Math.Max(0, diesel - 3.5) * 0.06;

    /// <summary>Per-order cost for one delivery type (reference unit()).</summary>
    public static UnitCost Unit(DeliveryType type, TypeInputs s, double diesel, UnitModifiers? m = null)
    {
        m ??= UnitModifiers.None;
        switch (type)
        {
            case DeliveryType.Pickup:
                return new UnitCost(0.12 * diesel / 3.7, PickupLabor, 0, PickupOverhead);

            case DeliveryType.Shipped:
                return new UnitCost(
                    CarrierBaseCharge * (CarrierSurcharge(diesel) + (m.SurchargeCap ? 0 : CarrierOverIndexSurcharge)),
                    0,
                    CarrierBaseCharge * (1 + s.SplitRate * 0.6),
                    0);

            case DeliveryType.SameDay:
            case DeliveryType.Express:
                var milesPerOrder = (s.MilesPerOrder ?? throw MissingRoute(type)) * m.MilesPerOrderMultiplier;
                var ordersPerRoute = (s.OrdersPerRoute ?? throw MissingRoute(type))
                    * (type == DeliveryType.Express ? m.ExpressOrdersPerRouteMultiplier : 1);
                var split = s.SplitRate * m.SplitRateMultiplier;
                var routeHours = type == DeliveryType.Express ? ExpressRouteHours : SameDayRouteHours;
                return new UnitCost(
                    milesPerOrder * (diesel / MilesPerGallon + FuelLinkedMileagePay * Math.Max(0, diesel - 3)) * (1 + split),
                    routeHours * HourlyWage / ordersPerRoute * (1 + 0.5 * split),
                    0,
                    milesPerOrder * VehicleCostPerMile * (1 + split));

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    /// <summary>
    /// One week of delivery cost at current-week volumes, with a diesel shift and
    /// levers applied (reference weekly()).
    /// </summary>
    public WeeklyResult Weekly(Levers levers, double dieselShift, IReadOnlyCollection<string>? regions = null)
    {
        levers.Validate();
        LeverLimits.EnsureInRange(nameof(dieselShift), dieselShift, LeverLimits.DieselShiftMax, LeverLimits.DieselShiftMin);

        var m = levers.ToModifiers();
        double cost = 0, fuel = 0, orders = 0, fee = 0, incentive = 0;
        var byType = DeliveryTypes.All.ToDictionary(t => t, _ => (Cost: 0d, Orders: 0d));

        foreach (var region in ResolveRegions(regions))
        {
            var b = DeliveryTypes.All.ToDictionary(t => t, t => inputs.Get(inputs.CurrentWeek, region, t));
            var diesel = b[DeliveryType.Express].Diesel + dieselShift;
            var o = b.ToDictionary(kv => kv.Key, kv => kv.Value.Orders);

            var moved = (o[DeliveryType.SameDay] + o[DeliveryType.Express]) * levers.Pickup;
            o[DeliveryType.SameDay] *= 1 - levers.Pickup;
            o[DeliveryType.Express] *= 1 - levers.Pickup;
            o[DeliveryType.Pickup] += moved;
            incentive += moved * PickupIncentivePerOrder;

            // A higher Express fee loses up to 4% of Express orders; 70% of those move to Same-day.
            var lostExpress = o[DeliveryType.Express] * Math.Max(-0.04, 0.04 * levers.Fee);
            o[DeliveryType.Express] -= lostExpress;
            o[DeliveryType.SameDay] += lostExpress * 0.7;

            if (levers.Basket)
            {
                var belowBasket = o[DeliveryType.Express] * 0.05;
                o[DeliveryType.Express] -= belowBasket;
                o[DeliveryType.SameDay] += belowBasket * 0.6;
            }

            fee += o[DeliveryType.Express] * levers.Fee;

            foreach (var type in DeliveryTypes.All)
            {
                var u = Unit(type, b[type], diesel, m);
                var c = u.Total * o[type];
                cost += c;
                fuel += u.Fuel * o[type];
                orders += o[type];
                var acc = byType[type];
                byType[type] = (acc.Cost + c, acc.Orders + o[type]);
            }
        }

        return new WeeklyResult(
            cost, fuel, orders, fee, incentive,
            byType.ToDictionary(kv => kv.Key, kv => new TypeTotals(kv.Value.Cost, kv.Value.Orders)));
    }

    /// <summary>
    /// Quarter view: fuel shock for <paramref name="durationWeeks"/> weeks, levers
    /// active for all 13 weeks (reference scenario()). Defaults to the whole network.
    /// </summary>
    public ScenarioResult Scenario(Levers levers, double dieselShift, int durationWeeks, IReadOnlyCollection<string>? regions = null)
    {
        if (durationWeeks is < LeverLimits.DurationMinWeeks or > LeverLimits.DurationMaxWeeks)
        {
            throw new ArgumentOutOfRangeException(nameof(durationWeeks), durationWeeks,
                $"Duration must be between {LeverLimits.DurationMinWeeks} and {LeverLimits.DurationMaxWeeks} weeks.");
        }

        var resolved = ResolveRegions(regions);
        var baseline = Weekly(Levers.None, 0, resolved);
        var shock = Weekly(Levers.None, dieselShift, resolved);
        var leversOnly = Weekly(levers, 0, resolved);
        var both = Weekly(levers, dieselShift, resolved);

        var shockQuarter = (shock.Cost - baseline.Cost) * durationWeeks;
        var scenarioQuarter = (both.Cost - baseline.Cost) * durationWeeks
            + (leversOnly.Cost - baseline.Cost) * (QuarterWeeks - durationWeeks)
            + both.PickupIncentiveCost * QuarterWeeks
            - both.ExpressFeeRevenue * QuarterWeeks;

        return new ScenarioResult(
            dieselShift,
            durationWeeks,
            levers,
            resolved,
            baseline.Cost,
            shock.Cost,
            leversOnly.Cost,
            both.Cost,
            shockQuarter,
            scenarioQuarter,
            -scenarioQuarter,
            shockQuarter > 0 ? (shockQuarter - scenarioQuarter) / shockQuarter : null,
            ExpressWithin3HoursPct(levers),
            97.5 - 1.5 * levers.Batch + 0.5 * levers.Closer - (levers.Split > 0.5 ? 0.5 : 0),
            100 - (levers.Basket ? 5 : 0) - 4 * Math.Max(0, levers.Fee));
    }

    /// <summary>
    /// Quarter view with different levers per region (for example, an action approved only for the
    /// Southeast). Weekly costs add up by region, so this is the sum of each region's scenario; with
    /// the same levers everywhere it equals <see cref="Scenario(Levers, double, int, IReadOnlyCollection{string}?)"/>.
    /// Express within 3 hours is the lowest region's (the guardrail must hold everywhere); promise
    /// kept and availability are weighted by orders.
    /// </summary>
    public ScenarioResult Scenario(IReadOnlyDictionary<string, Levers> leversByRegion, double dieselShift, int durationWeeks)
    {
        if (leversByRegion.Count == 0)
        {
            throw new ArgumentException("At least one region is required.", nameof(leversByRegion));
        }

        var parts = leversByRegion.Select(kv => (Result: Scenario(kv.Value, dieselShift, durationWeeks, [kv.Key]),
            Orders: Weekly(Levers.None, 0, [kv.Key]).Orders)).ToArray();
        var totalOrders = parts.Sum(p => p.Orders);
        double Weighted(Func<ScenarioResult, double> f) => parts.Sum(p => f(p.Result) * p.Orders) / totalOrders;
        double Sum(Func<ScenarioResult, double> f) => parts.Sum(p => f(p.Result));

        var shockQuarter = Sum(r => r.FuelShockQuarterCost);
        var scenarioQuarter = Sum(r => r.ScenarioQuarterCost);
        var combined = leversByRegion.Values.Aggregate(Levers.None, (a, l) => new Levers
        {
            Batch = Math.Max(a.Batch, l.Batch),
            Closer = Math.Max(a.Closer, l.Closer),
            Split = Math.Max(a.Split, l.Split),
            Pickup = Math.Max(a.Pickup, l.Pickup),
            Fee = Math.Max(a.Fee, l.Fee),
            Basket = a.Basket || l.Basket,
            Cap = a.Cap || l.Cap,
        });

        return new ScenarioResult(
            dieselShift,
            durationWeeks,
            combined,
            parts.SelectMany(p => p.Result.Regions).ToArray(),
            Sum(r => r.BaselineWeeklyCost),
            Sum(r => r.ShockWeeklyCost),
            Sum(r => r.LeversWeeklyCost),
            Sum(r => r.CombinedWeeklyCost),
            shockQuarter,
            scenarioQuarter,
            -scenarioQuarter,
            shockQuarter > 0 ? (shockQuarter - scenarioQuarter) / shockQuarter : null,
            parts.Min(p => p.Result.ExpressWithin3HoursPct),
            Weighted(r => r.PromiseKeptPct),
            Weighted(r => r.AvailabilityPct));
    }

    /// <summary>Modeled share of Express orders delivered within 3 hours (illustrative, not telemetry).</summary>
    public static double ExpressWithin3HoursPct(Levers levers) => 100 - 45 * levers.Batch - (levers.Basket ? 2 : 0);

    /// <summary>Guardrail findings for a lever set (reference guard()).</summary>
    public static IReadOnlyList<GuardrailFinding> Guard(Levers levers)
    {
        var findings = new List<GuardrailFinding>();
        if (ExpressWithin3HoursPct(levers) < ExpressWithin3HoursThresholdPct)
        {
            findings.Add(new("EXPRESS_3H_BELOW_80", GuardrailSeverity.Blocking,
                "Breaks the guardrail: fewer than 80% of Express orders delivered within 3 hours."));
        }

        if (levers.Fee > 1.5)
        {
            findings.Add(new("EXPRESS_FEE_ABOVE_1_50", GuardrailSeverity.ApprovalRequired,
                "Express fee rise above $1.50 may push stretched shoppers away. Needs eCommerce approval."));
        }

        if (levers.Closer > 0.3)
        {
            findings.Add(new("CLOSER_STORE_STOCK_CHECK", GuardrailSeverity.Warning,
                "Closer-store share above 30% needs a stock check at those stores."));
        }

        if (levers.Basket)
        {
            findings.Add(new("MIN_BASKET_RURAL_ONLY", GuardrailSeverity.Constraint,
                "Minimum basket applies to rural zips only, never urban zones."));
        }

        if (levers.Pickup > 0.06)
        {
            findings.Add(new("PICKUP_INCENTIVE_PAYBACK", GuardrailSeverity.Warning,
                "Pickup incentive cost is rising. Check payback."));
        }

        return findings;
    }

    /// <summary>Checks the Express promise guardrail: at least 80% delivered within 3 hours.</summary>
    public static GuardrailCheck CheckGuardrail(Levers levers)
    {
        levers.Validate();
        var findings = Guard(levers);
        return new GuardrailCheck(
            ExpressWithin3HoursPct(levers),
            ExpressWithin3HoursThresholdPct,
            findings.All(f => f.Severity != GuardrailSeverity.Blocking),
            findings);
    }

    /// <summary>
    /// Attributes the week-over-week cost-per-order change to fuel price, route
    /// density and split shipments (generalizes the reference "story numbers").
    /// </summary>
    public VarianceResult ExplainVariance(string region, DeliveryType type, int? priorWeek = null, int? currentWeek = null)
    {
        var canonical = inputs.CanonicalRegion(region);
        var prior = priorWeek ?? inputs.PriorWeek;
        var current = currentWeek ?? inputs.CurrentWeek;
        var p = inputs.Get(prior, canonical, type);
        var c = inputs.Get(current, canonical, type);

        var c0 = Unit(type, p, p.Diesel).Total;
        var c1 = Unit(type, p, c.Diesel).Total;
        var c2 = Unit(type, p with { OrdersPerRoute = c.OrdersPerRoute, MilesPerOrder = c.MilesPerOrder }, c.Diesel).Total;
        var c3 = Unit(type, c, c.Diesel).Total;

        return new VarianceResult(canonical, type, prior, current, c0, c1, c2, c3, p, c);
    }

    /// <summary>Weekly saving of a lever set versus no levers, net of incentives and fee revenue.</summary>
    public double WeeklySaving(Levers levers, IReadOnlyCollection<string>? regions = null)
    {
        var resolved = ResolveRegions(regions);
        var baseline = Weekly(Levers.None, 0, resolved);
        var withLevers = Weekly(levers, 0, resolved);
        return baseline.Cost - withLevers.Cost - withLevers.PickupIncentiveCost + withLevers.ExpressFeeRevenue;
    }

    private IReadOnlyList<string> ResolveRegions(IReadOnlyCollection<string>? regions)
    {
        if (regions is null)
        {
            return inputs.Regions;
        }

        if (regions.Count == 0)
        {
            throw new ArgumentException("Specify at least one region, or omit regions for the whole network.", nameof(regions));
        }

        return regions.Select(inputs.CanonicalRegion).Distinct().ToArray();
    }

    private static InvalidOperationException MissingRoute(DeliveryType type) =>
        new($"{type.ToSeedName()} inputs require orders per route and miles per order.");
}
