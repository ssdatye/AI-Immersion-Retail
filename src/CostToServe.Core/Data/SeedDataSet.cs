using CostToServe.Core.Engine;
using CostToServe.Core.Model;

namespace CostToServe.Core.Data;

/// <summary>
/// The illustrative seed data set (data/seed), loaded from CSV and validated.
/// Weekly engine inputs are aggregated from daily facts:
/// orders = sum; split rate = order-weighted mean; diesel = mean daily price;
/// orders per route = orders / routes; miles per order = miles / orders.
/// </summary>
public sealed class SeedDataSet
{
    private readonly Dictionary<string, ZoneRecord> _zonesByName;

    private SeedDataSet(
        string directory,
        IReadOnlyList<ZoneRecord> zones,
        IReadOnlyList<OrderDay> orders,
        IReadOnlyList<RouteDay> routes,
        IReadOnlyList<FuelDay> fuel,
        IReadOnlyList<CostDay> costs,
        IReadOnlyList<FuelOutlookWeek> fuelOutlook,
        IReadOnlyList<MarketEvent> marketEvents,
        IReadOnlyList<CarrierInvoiceWeek> carrierInvoices,
        IReadOnlyList<PlaybookAction> playbook)
    {
        Directory = directory;
        Zones = zones;
        Orders = orders;
        Routes = routes;
        Fuel = fuel;
        Costs = costs;
        FuelOutlook = fuelOutlook;
        MarketEvents = marketEvents;
        CarrierInvoices = carrierInvoices;
        Playbook = playbook;

        _zonesByName = zones.ToDictionary(z => z.Zone, StringComparer.OrdinalIgnoreCase);
        Regions = zones.Select(z => z.Region).Distinct().ToArray();
        Dates = orders.Select(o => o.Date).Distinct().Order().ToArray();
        Validate();
        Weeks = BuildWeeks();
        WeeklyInputs = BuildWeeklyInputs();
        _zoneInputs = BuildZoneInputs();
    }

    private readonly Dictionary<(int Week, string Zone, DeliveryType Type), TypeInputs> _zoneInputs;

    public string Directory { get; }

    public IReadOnlyList<string> Regions { get; }

    public IReadOnlyList<DateOnly> Dates { get; }

    public IReadOnlyList<DemoWeek> Weeks { get; }

    public IReadOnlyList<ZoneRecord> Zones { get; }

    public IReadOnlyList<OrderDay> Orders { get; }

    public IReadOnlyList<RouteDay> Routes { get; }

    public IReadOnlyList<FuelDay> Fuel { get; }

    public IReadOnlyList<CostDay> Costs { get; }

    public IReadOnlyList<FuelOutlookWeek> FuelOutlook { get; }

    public IReadOnlyList<MarketEvent> MarketEvents { get; }

    public IReadOnlyList<CarrierInvoiceWeek> CarrierInvoices { get; }

    public IReadOnlyList<PlaybookAction> Playbook { get; }

    /// <summary>Region-level weekly inputs for the cost engine.</summary>
    public WeeklyInputSet WeeklyInputs { get; }

    /// <summary>The seed tables, in load order. Each is one CSV in data/seed and one table in Azure SQL.</summary>
    public static IReadOnlyList<string> TableFiles { get; } =
    [
        "zones.csv", "orders_daily.csv", "routes_daily.csv", "fuel_prices_daily.csv", "cost_per_order_daily.csv",
        "fuel_outlook_weekly.csv", "market_events.csv", "carrier_invoices_weekly.csv", "action_playbook.csv", "decision_log.csv",
    ];

    /// <summary>Reads every seed table from a directory of CSVs as raw text.</summary>
    public static IReadOnlyDictionary<string, SeedTable> ReadTables(string directory) =>
        TableFiles.ToDictionary(f => f, f => CsvTable.ReadRaw(Path.Combine(directory, f)), StringComparer.Ordinal);

    public static SeedDataSet Load(string directory) => Load(ReadTables(directory), directory);

    /// <summary>Loads from raw tables keyed by CSV file name (from CSV files or the Azure SQL copy).</summary>
    public static SeedDataSet Load(IReadOnlyDictionary<string, SeedTable> tables, string source)
    {
        var directory = source;
        SeedTable P(string file) => tables.TryGetValue(file, out var t) ? t : throw new InvalidDataException($"Seed table '{file}' not found in {source}.");

        var zones = CsvTable.Rows(P("zones.csv"), "region", "zone", "density", "miles_to_store")
            .Select(r => new ZoneRecord(r.Text("region"), r.Text("zone"), r.Text("density"), r.Number("miles_to_store")))
            .ToArray();

        var orders = CsvTable.Rows(P("orders_daily.csv"), "date", "region", "zone", "delivery_type", "orders", "avg_basket_usd", "split_shipment_rate")
            .Select(r => new OrderDay(r.Date("date"), r.Text("region"), r.Text("zone"), Type(r), r.Number("orders"), r.Number("avg_basket_usd"), r.Number("split_shipment_rate")))
            .ToArray();

        var routes = CsvTable.Rows(P("routes_daily.csv"), "date", "region", "zone", "delivery_type", "routes", "miles", "stops", "orders_per_route", "miles_per_order")
            .Select(r => new RouteDay(r.Date("date"), r.Text("region"), r.Text("zone"), Type(r), r.Number("routes"), r.Number("miles"), r.Number("stops"), r.Number("orders_per_route"), r.Number("miles_per_order")))
            .ToArray();

        var fuel = CsvTable.Rows(P("fuel_prices_daily.csv"), "date", "region", "diesel_usd_gal", "gasoline_usd_gal")
            .Select(r => new FuelDay(r.Date("date"), r.Text("region"), r.Number("diesel_usd_gal"), r.Number("gasoline_usd_gal")))
            .ToArray();

        var costs = CsvTable.Rows(P("cost_per_order_daily.csv"), "date", "region", "zone", "delivery_type", "orders", "fuel_cost_usd", "labor_cost_usd", "carrier_fees_usd", "vehicle_cost_usd", "cost_per_order_usd")
            .Select(r => new CostDay(r.Date("date"), r.Text("region"), r.Text("zone"), Type(r), r.Number("orders"), r.Number("fuel_cost_usd"), r.Number("labor_cost_usd"), r.Number("carrier_fees_usd"), r.Number("vehicle_cost_usd"), r.Number("cost_per_order_usd")))
            .ToArray();

        var outlook = CsvTable.Rows(P("fuel_outlook_weekly.csv"), "week_start", "region", "diesel_low", "diesel_base", "diesel_high")
            .Select(r => new FuelOutlookWeek(r.Date("week_start"), r.Text("region"), r.Number("diesel_low"), r.Number("diesel_base"), r.Number("diesel_high")))
            .ToArray();

        var events = CsvTable.Rows(P("market_events.csv"), "date", "event_type", "region", "expected_diesel_effect_usd_gal", "credibility")
            .Select(r => new MarketEvent(r.Date("date"), r.Text("event_type"), r.Text("region"), r.Text("expected_diesel_effect_usd_gal"), r.Text("credibility")))
            .ToArray();

        var invoices = CsvTable.Rows(P("carrier_invoices_weekly.csv"), "week_start", "region", "carrier", "base_charge_usd", "fuel_surcharge_pct", "contract_index_surcharge_pct", "index")
            .Select(r => new CarrierInvoiceWeek(r.Date("week_start"), r.Text("region"), r.Text("carrier"), r.Number("base_charge_usd"), r.Number("fuel_surcharge_pct"), r.Number("contract_index_surcharge_pct"), r.Text("index")))
            .ToArray();

        var playbook = CsvTable.Rows(P("action_playbook.csv"), "action_id", "action", "lever", "effect", "customer_impact", "approver", "guardrail")
            .Select(r => new PlaybookAction(r.Text("action_id"), r.Text("action"), r.Text("lever"), r.Text("effect"), r.Text("customer_impact"), r.Text("approver"), r.Text("guardrail")))
            .ToArray();

        _ = CsvTable.Rows(P("decision_log.csv"), "timestamp", "action_id", "approver", "decision", "reason", "next_day_result");

        return new SeedDataSet(directory, zones, orders, routes, fuel, costs, outlook, events, invoices, playbook);

        static DeliveryType Type(CsvRow row) =>
            DeliveryTypes.TryParse(row.Text("delivery_type"), out var type) ? type : throw row.Invalid("delivery_type", "a known delivery type");
    }

    public string CanonicalRegion(string region) => WeeklyInputs.CanonicalRegion(region);

    /// <summary>Zones of a region in seed order (zones.csv).</summary>
    public IReadOnlyList<ZoneRecord> ZonesIn(string region)
    {
        var canonical = CanonicalRegion(region);
        return Zones.Where(z => z.Region == canonical).ToArray();
    }

    /// <summary>Zone-level weekly inputs, aggregated like the region-level inputs.</summary>
    public TypeInputs GetZoneInputs(int week, string zone, DeliveryType type) =>
        _zoneInputs.TryGetValue((week, ResolveZone(zone).Zone, type), out var value)
            ? value
            : throw new ArgumentException($"No inputs for week {week}, zone '{zone}', type {type.ToSeedName()}.");

    public ZoneRecord ResolveZone(string zone) =>
        _zonesByName.TryGetValue(zone, out var record)
            ? record
            : throw new ArgumentException($"Unknown zone '{zone}'.", nameof(zone));

    /// <summary>Cost per order from the daily cost facts (sum of cost / sum of orders).</summary>
    public CostFactsResult QueryCostPerOrder(CostQuery query)
    {
        var lastWeek = Weeks[^1];
        var from = query.From ?? lastWeek.Start;
        var to = query.To ?? lastWeek.End;
        if (from > to)
        {
            throw new ArgumentException($"Start date {from:yyyy-MM-dd} is after end date {to:yyyy-MM-dd}.", nameof(query));
        }

        if (from < Dates[0] || to > Dates[^1])
        {
            throw new ArgumentException($"Date range must fall within {Dates[0]:yyyy-MM-dd} to {Dates[^1]:yyyy-MM-dd}.", nameof(query));
        }

        var region = string.IsNullOrWhiteSpace(query.Region) ? null : CanonicalRegion(query.Region);
        var zone = string.IsNullOrWhiteSpace(query.Zone) ? null : ResolveZone(query.Zone);
        if (zone is not null && region is not null && !string.Equals(zone.Region, region, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Zone '{zone.Zone}' is in {zone.Region}, not {region}.", nameof(query));
        }

        region ??= zone?.Region;
        double orders = 0, fuel = 0, labor = 0, carrier = 0, vehicle = 0;
        foreach (var day in Costs)
        {
            if (day.Date < from || day.Date > to
                || (region is not null && day.Region != region)
                || (zone is not null && day.Zone != zone.Zone)
                || (query.DeliveryType is { } type && day.DeliveryType != type))
            {
                continue;
            }

            orders += day.Orders;
            fuel += day.FuelCost;
            labor += day.LaborCost;
            carrier += day.CarrierCost;
            vehicle += day.VehicleCost;
        }

        return new CostFactsResult(region, zone?.Zone, query.DeliveryType, from, to, to.DayNumber - from.DayNumber + 1, orders, fuel, labor, carrier, vehicle);
    }

    private void Validate()
    {
        if (Dates.Count == 0)
        {
            throw new InvalidDataException("orders_daily.csv contains no rows.");
        }

        for (var i = 1; i < Dates.Count; i++)
        {
            if (Dates[i].DayNumber - Dates[i - 1].DayNumber != 1)
            {
                throw new InvalidDataException($"orders_daily.csv has a date gap before {Dates[i]:yyyy-MM-dd}.");
            }
        }

        void CheckZone(string file, string region, string zone)
        {
            if (!_zonesByName.TryGetValue(zone, out var z) || z.Region != region)
            {
                throw new InvalidDataException($"{file} references zone '{zone}' in region '{region}', which zones.csv does not define.");
            }
        }

        foreach (var o in Orders)
        {
            CheckZone("orders_daily.csv", o.Region, o.Zone);
        }

        foreach (var r in Routes)
        {
            CheckZone("routes_daily.csv", r.Region, r.Zone);
            if (!r.DeliveryType.IsRouted() || r.Routes <= 0)
            {
                throw new InvalidDataException($"routes_daily.csv has an invalid route row for {r.Zone} {r.DeliveryType.ToSeedName()} on {r.Date:yyyy-MM-dd}.");
            }
        }

        foreach (var c in Costs)
        {
            CheckZone("cost_per_order_daily.csv", c.Region, c.Zone);
        }

        var fuelKeys = Fuel.Select(f => (f.Date, f.Region)).ToHashSet();
        var missingFuel = Dates.SelectMany(d => Regions.Select(r => (Date: d, Region: r))).FirstOrDefault(k => !fuelKeys.Contains(k));
        if (missingFuel != default)
        {
            throw new InvalidDataException($"fuel_prices_daily.csv has no price for {missingFuel.Region} on {missingFuel.Date:yyyy-MM-dd}.");
        }
    }

    private DemoWeek[] BuildWeeks()
    {
        var first = Dates[0];
        return Dates
            .GroupBy(d => (d.DayNumber - first.DayNumber) / 7 + 1)
            .Where(g => g.Count() == 7)
            .Select(g => new DemoWeek(g.Key, g.Min(), g.Max()))
            .ToArray();
    }

    private int WeekNumber(DateOnly date) => (date.DayNumber - Dates[0].DayNumber) / 7 + 1;

    private Dictionary<(int Week, string Zone, DeliveryType Type), TypeInputs> BuildZoneInputs()
    {
        var complete = Weeks.Select(w => w.Number).ToHashSet();
        var orderAgg = new Dictionary<(int, string, DeliveryType), (double Orders, double SplitWeighted, string Region)>();
        foreach (var o in Orders)
        {
            var key = (WeekNumber(o.Date), o.Zone, o.DeliveryType);
            orderAgg.TryGetValue(key, out var acc);
            orderAgg[key] = (acc.Orders + o.Orders, acc.SplitWeighted + o.Orders * o.SplitRate, o.Region);
        }

        var routeAgg = new Dictionary<(int, string, DeliveryType), (double Routes, double Miles)>();
        foreach (var r in Routes)
        {
            var key = (WeekNumber(r.Date), r.Zone, r.DeliveryType);
            routeAgg.TryGetValue(key, out var acc);
            routeAgg[key] = (acc.Routes + r.Routes, acc.Miles + r.Miles);
        }

        var inputs = new Dictionary<(int Week, string Zone, DeliveryType Type), TypeInputs>();
        foreach (var ((week, zone, type), agg) in orderAgg)
        {
            if (!complete.Contains(week) || agg.Orders <= 0)
            {
                continue;
            }

            double? ordersPerRoute = null, milesPerOrder = null;
            if (type.IsRouted() && routeAgg.TryGetValue((week, zone, type), out var route))
            {
                ordersPerRoute = agg.Orders / route.Routes;
                milesPerOrder = route.Miles / agg.Orders;
            }

            // Diesel is priced per region; zones use their region's weekly average.
            var diesel = WeeklyInputs.Get(week, agg.Region, type).Diesel;
            inputs[(week, zone, type)] = new TypeInputs(agg.Orders, agg.SplitWeighted / agg.Orders, diesel, ordersPerRoute, milesPerOrder);
        }

        return inputs;
    }

    private WeeklyInputSet BuildWeeklyInputs()
    {
        if (Weeks.Count < 2)
        {
            throw new InvalidDataException("At least two complete weeks of seed data are required.");
        }

        var complete = Weeks.Select(w => w.Number).ToHashSet();

        var orderAgg = new Dictionary<(int, string, DeliveryType), (double Orders, double SplitWeighted)>();
        foreach (var o in Orders)
        {
            var key = (WeekNumber(o.Date), o.Region, o.DeliveryType);
            orderAgg.TryGetValue(key, out var acc);
            orderAgg[key] = (acc.Orders + o.Orders, acc.SplitWeighted + o.Orders * o.SplitRate);
        }

        var routeAgg = new Dictionary<(int, string, DeliveryType), (double Routes, double Miles)>();
        foreach (var r in Routes)
        {
            var key = (WeekNumber(r.Date), r.Region, r.DeliveryType);
            routeAgg.TryGetValue(key, out var acc);
            routeAgg[key] = (acc.Routes + r.Routes, acc.Miles + r.Miles);
        }

        var dieselAgg = new Dictionary<(int, string), (double Sum, int Days)>();
        foreach (var f in Fuel)
        {
            var key = (WeekNumber(f.Date), f.Region);
            dieselAgg.TryGetValue(key, out var acc);
            dieselAgg[key] = (acc.Sum + f.DieselUsdPerGallon, acc.Days + 1);
        }

        var inputs = new Dictionary<(int Week, string Region, DeliveryType Type), TypeInputs>();
        foreach (var ((week, region, type), agg) in orderAgg)
        {
            if (!complete.Contains(week) || agg.Orders <= 0)
            {
                continue;
            }

            var (dieselSum, days) = dieselAgg[(week, region)];
            double? ordersPerRoute = null, milesPerOrder = null;
            if (type.IsRouted() && routeAgg.TryGetValue((week, region, type), out var route))
            {
                ordersPerRoute = agg.Orders / route.Routes;
                milesPerOrder = route.Miles / agg.Orders;
            }

            inputs[(week, region, type)] = new TypeInputs(agg.Orders, agg.SplitWeighted / agg.Orders, dieselSum / days, ordersPerRoute, milesPerOrder);
        }

        return new WeeklyInputSet(Regions, inputs);
    }
}

public sealed record CostQuery(
    string? Region = null,
    string? Zone = null,
    DeliveryType? DeliveryType = null,
    DateOnly? From = null,
    DateOnly? To = null);

public sealed record CostFactsResult(
    string? Region,
    string? Zone,
    DeliveryType? DeliveryType,
    DateOnly From,
    DateOnly To,
    int Days,
    double Orders,
    double FuelCost,
    double LaborCost,
    double CarrierCost,
    double VehicleCost)
{
    public string Scope => Zone ?? Region ?? "Network";

    public double TotalCost => FuelCost + LaborCost + CarrierCost + VehicleCost;

    public double CostPerOrder => Orders > 0 ? TotalCost / Orders : 0;
}
