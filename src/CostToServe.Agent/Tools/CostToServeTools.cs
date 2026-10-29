using System.ComponentModel;
using System.Runtime.CompilerServices;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Security;
using CostToServe.Agent.Telemetry;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;
using Microsoft.Extensions.AI;

namespace CostToServe.Agent.Tools;

/// <summary>
/// The agent's tools. Every number the agent reports comes from here, computed by the
/// deterministic engine or read from seed facts. The caller is fixed by the host when
/// the tools are created; no tool accepts identity, role or scope from the model.
/// </summary>
public sealed class CostToServeTools(
    SeedDataSet data,
    CostEngine engine,
    CallerContext caller,
    IDecisionLog decisionLog,
    IDecisionConfirmer confirmer,
    TimeProvider? timeProvider = null,
    Action<string, string, object?>? onToolCompleted = null,
    ActionCatalog? actions = null)
{
    public const string GetCostPerOrderName = "get_cost_per_order";
    public const string ExplainVarianceName = "explain_variance";
    public const string RunScenarioName = "run_scenario";
    public const string CheckGuardrailName = "check_guardrail";
    public const string GetActionPlaybookName = "get_action_playbook";
    public const string LogDecisionName = "log_decision";
    public const string GetExecutionResultsName = "get_execution_results";

    private const string DataNotice = "Illustrative demo data; not real Walmart data.";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ActionCatalog _actions = actions ?? ActionCatalog.Default;

    private static readonly Dictionary<string, string> ToolNames = new()
    {
        [nameof(GetCostPerOrder)] = GetCostPerOrderName,
        [nameof(ExplainVariance)] = ExplainVarianceName,
        [nameof(RunScenario)] = RunScenarioName,
        [nameof(CheckGuardrail)] = CheckGuardrailName,
        [nameof(GetActionPlaybook)] = GetActionPlaybookName,
        [nameof(LogDecisionAsync)] = LogDecisionName,
        [nameof(GetExecutionResults)] = GetExecutionResultsName,
    };

    public CallerContext Caller => caller;

    public IList<AITool> Create() =>
    [
        AIFunctionFactory.Create(
            ([Description("Region name (Southeast, Midwest, West). Omit for the caller's default scope.")] string? region = null,
             [Description("Zone name, for example 'Southeast Zone 1'. Optional.")] string? zone = null,
             [Description("Pickup, Same-day, Express or Shipped. Omit for all delivery types.")] string? delivery_type = null,
             [Description("Start date, yyyy-MM-dd. Omit for the latest complete week.")] string? start_date = null,
             [Description("End date, yyyy-MM-dd. Omit for the latest complete week.")] string? end_date = null) =>
                GetCostPerOrder(region, zone, delivery_type, start_date, end_date),
            new AIFunctionFactoryOptions
            {
                Name = GetCostPerOrderName,
                Description = "Actual cost per order from daily cost facts, with fuel, labor, carrier and vehicle components.",
            }),
        AIFunctionFactory.Create(
            ([Description("Region name (Southeast, Midwest, West).")] string region,
             [Description("Pickup, Same-day, Express or Shipped.")] string delivery_type) =>
                ExplainVariance(region, delivery_type),
            new AIFunctionFactoryOptions
            {
                Name = ExplainVarianceName,
                Description = "Explains the week-over-week change in cost per order, split into fuel price, route density and split shipments.",
            }),
        AIFunctionFactory.Create(
            ([Description("Diesel price change in USD per gallon, -0.5 to 1.0.")] double diesel_change_usd_per_gallon,
             [Description("Weeks the diesel change lasts, 1 to 13.")] int duration_weeks,
             [Description("Region to model. Omit for the caller's default scope (whole network for finance).")] string? region = null,
             [Description("Playbook actions to apply together in every modeled region, comma-separated, for example 'A1,A2'. Each uses " +
                          "that region's decided setting (proposed or approved), otherwise the playbook setting. Combined with the levers below.")] string? action_ids = null,
             [Description("True to include the current plan: actions approved or executed for each region, applied only in that region.")] bool apply_decided_plan = false,
             [Description("Share of Express orders batched into 2-hour windows, 0 to 0.6 (A1 uses 0.35).")] double batch_express_share = 0,
             [Description("Share of delivery orders filled from a closer store, 0 to 0.4 (A2 uses 0.2).")] double closer_store_share = 0,
             [Description("Fractional reduction in split shipments, 0 to 0.6.")] double split_shipment_reduction = 0,
             [Description("Share of Same-day and Express orders nudged to Pickup, 0 to 0.1.")] double pickup_shift_share = 0,
             [Description("Express fee change in USD per order, -1 to 3.")] double express_fee_change_usd = 0,
             [Description("Minimum basket for Express in rural zips.")] bool rural_minimum_basket = false,
             [Description("Cap carrier fuel surcharge at the contract index.")] bool carrier_surcharge_cap = false) =>
                RunScenario(diesel_change_usd_per_gallon, duration_weeks, region, action_ids, apply_decided_plan, new Levers
                {
                    Batch = batch_express_share,
                    Closer = closer_store_share,
                    Split = split_shipment_reduction,
                    Pickup = pickup_shift_share,
                    Fee = express_fee_change_usd,
                    Basket = rural_minimum_basket,
                    Cap = carrier_surcharge_cap,
                }),
            new AIFunctionFactoryOptions
            {
                Name = RunScenarioName,
                Description = "Models a diesel price change with optional levers over a 13-week quarter: added fuel cost, " +
                              "net operating income impact, share of the shock offset, service levels and guardrail check.",
            }),
        AIFunctionFactory.Create(
            ([Description("Playbook actions to check together, comma-separated, for example 'A1,A2'.")] string? action_ids = null,
             [Description("Region whose decided settings to use. Omit for the caller's default scope.")] string? region = null,
             [Description("Share of Express orders batched, 0 to 0.6.")] double batch_express_share = 0,
             [Description("Share filled from a closer store, 0 to 0.4.")] double closer_store_share = 0,
             [Description("Fractional reduction in split shipments, 0 to 0.6.")] double split_shipment_reduction = 0,
             [Description("Share nudged to Pickup, 0 to 0.1.")] double pickup_shift_share = 0,
             [Description("Express fee change in USD, -1 to 3.")] double express_fee_change_usd = 0,
             [Description("Minimum basket for Express in rural zips.")] bool rural_minimum_basket = false,
             [Description("Cap carrier fuel surcharge at the contract index.")] bool carrier_surcharge_cap = false) =>
                CheckGuardrail(action_ids, region, new Levers
                {
                    Batch = batch_express_share,
                    Closer = closer_store_share,
                    Split = split_shipment_reduction,
                    Pickup = pickup_shift_share,
                    Fee = express_fee_change_usd,
                    Basket = rural_minimum_basket,
                    Cap = carrier_surcharge_cap,
                }),
            new AIFunctionFactoryOptions
            {
                Name = CheckGuardrailName,
                Description = "Checks levers against guardrails. The hard guardrail: at least 80% of Express orders delivered within 3 hours.",
            }),
        AIFunctionFactory.Create(
            ([Description("Region whose decisions and savings to show. Omit for the caller's default scope; for the whole network, " +
                          "each action lists its status per region in ByRegion.")] string? region = null) =>
                GetActionPlaybook(region),
            new AIFunctionFactoryOptions
            {
                Name = GetActionPlaybookName,
                Description = "Lists the approved action playbook with modeled weekly savings, customer impact, approver, guardrail " +
                              "status and decisions. Decisions are per region. Recommend only actions from this list.",
            }),
        AIFunctionFactory.Create(
            () => GetExecutionResults(),
            new AIFunctionFactoryOptions
            {
                Name = GetExecutionResultsName,
                Description = "Modeled results of executed actions: Express cost per order in pilot zones versus control zones, " +
                              "and the weekly saving if scaled across the pilot region.",
            }),
        AIFunctionFactory.Create(
            ([Description("Playbook action ID, A1 to A6.")] string action_id,
             [Description("propose, approve, reject or execute.")] string decision,
             [Description("Reason. Required when rejecting.")] string? reason,
             [Description("Optional setting for the action's adjustable lever as a fraction when proposing or approving, " +
                          "for example 0.05 to shift 5% to Pickup for A6. Omit to keep the current setting. " +
                          "Only A1, A2, A4 and A6 are adjustable; get_action_playbook shows the range.")] double? setting,
             [Description("Region the decision applies to (Southeast, Midwest or West). Regional ops always decide for their own region. " +
                          "Finance must say which region unless exactly one region has the action proposed.")] string? region,
             CancellationToken cancellationToken) =>
                LogDecisionAsync(action_id, decision, reason, setting, region, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = LogDecisionName,
                Description = "Requests that a decision on a playbook action be logged for one region, optionally with a different setting. " +
                              "Proposing or approving again with a different setting changes it. The user must confirm in the app; " +
                              "only a result with status 'recorded' means it was logged.",
            }),
    ];

    public object GetCostPerOrder(string? region, string? zone, string? deliveryType, string? startDate, string? endDate) =>
        Guarded(() =>
        {
            ZoneRecord? zoneRecord = null;
            if (!string.IsNullOrWhiteSpace(zone))
            {
                zoneRecord = data.ResolveZone(zone);
                EnsureCanSee(zoneRecord.Region);
            }

            var scopedRegion = string.IsNullOrWhiteSpace(region) && zoneRecord is not null ? zoneRecord.Region : ScopedRegion(region);
            var facts = data.QueryCostPerOrder(new CostQuery(
                scopedRegion,
                zoneRecord?.Zone,
                string.IsNullOrWhiteSpace(deliveryType) ? null : DeliveryTypes.Parse(deliveryType),
                ParseDate(startDate, nameof(startDate)),
                ParseDate(endDate, nameof(endDate))));

            double PerOrder(double cost) => facts.Orders > 0 ? Usd(cost / facts.Orders) : 0;
            return new
            {
                facts.Scope,
                facts.Region,
                facts.Zone,
                DeliveryType = facts.DeliveryType?.ToSeedName() ?? "All",
                From = facts.From.ToString("yyyy-MM-dd"),
                To = facts.To.ToString("yyyy-MM-dd"),
                facts.Days,
                Orders = Math.Round(facts.Orders),
                TotalCostUsd = Math.Round(facts.TotalCost),
                CostPerOrderUsd = Usd(facts.CostPerOrder),
                PerOrderComponentsUsd = new
                {
                    Fuel = PerOrder(facts.FuelCost),
                    Labor = PerOrder(facts.LaborCost),
                    Carrier = PerOrder(facts.CarrierCost),
                    Vehicle = PerOrder(facts.VehicleCost),
                },
                Source = "cost_per_order_daily facts",
                Notice = DataNotice,
            };
        }, scopedRegionHint: region);

    public object ExplainVariance(string region, string deliveryType) =>
        Guarded(() =>
        {
            var canonical = data.CanonicalRegion(region);
            EnsureCanSee(canonical);
            var v = engine.ExplainVariance(canonical, DeliveryTypes.Parse(deliveryType));
            var prior = data.Weeks.Single(w => w.Number == v.PriorWeek);
            var current = data.Weeks.Single(w => w.Number == v.CurrentWeek);
            return new
            {
                v.Region,
                DeliveryType = v.DeliveryType.ToSeedName(),
                PriorWeek = $"{prior.Start:yyyy-MM-dd} to {prior.End:yyyy-MM-dd}",
                CurrentWeek = $"{current.Start:yyyy-MM-dd} to {current.End:yyyy-MM-dd}",
                PriorCostPerOrderUsd = Usd(v.PriorCostPerOrder),
                CurrentCostPerOrderUsd = Usd(v.CurrentCostPerOrder),
                ChangeUsd = Usd(v.Change),
                ChangePct = Pct(v.ChangePct),
                Drivers = new[]
                {
                    new { Driver = "Fuel price", EffectUsd = Usd(v.FuelPriceEffect), SharePct = Pct(v.FuelPriceShare) },
                    new { Driver = "Route density (orders per route, miles per order)", EffectUsd = Usd(v.RouteDensityEffect), SharePct = Pct(v.RouteDensityShare) },
                    new { Driver = "Split shipments", EffectUsd = Usd(v.SplitShipmentEffect), SharePct = Pct(v.SplitShipmentShare) },
                },
                Inputs = new
                {
                    DieselUsdPerGallon = new { Prior = Math.Round(v.PriorInputs.Diesel, 3), Current = Math.Round(v.CurrentInputs.Diesel, 3) },
                    OrdersPerRoute = Pair(v.PriorInputs.OrdersPerRoute, v.CurrentInputs.OrdersPerRoute, 2),
                    MilesPerOrder = Pair(v.PriorInputs.MilesPerOrder, v.CurrentInputs.MilesPerOrder, 2),
                    SplitShipmentRatePct = new { Prior = Pct(v.PriorInputs.SplitRate), Current = Pct(v.CurrentInputs.SplitRate) },
                },
                Method = "Modeled cost per order, re-costed step by step: fuel price, then route density, then split shipments.",
                Notice = DataNotice,
            };
        }, scopedRegionHint: region);

    public object RunScenario(double dieselChange, int durationWeeks, string? region, string? actionIds, Levers explicitLevers) =>
        RunScenario(dieselChange, durationWeeks, region, actionIds, applyDecidedPlan: false, explicitLevers);

    /// <param name="applyDecidedPlan">Adds each region's approved and executed actions, in that region only.</param>
    public object RunScenario(double dieselChange, int durationWeeks, string? region, string? actionIds, bool applyDecidedPlan, Levers explicitLevers) =>
        Guarded(() =>
        {
            var scopedRegion = ScopedRegion(region);
            var regions = scopedRegion is null ? data.Regions.ToArray() : [scopedRegion];
            var latest = LatestDecisions();
            var (actions, _) = CombineActions(actionIds, Levers.None, _actions);
            var plan = regions.ToDictionary(r => r, r => applyDecidedPlan ? PlanIds(r, latest) : []);
            var perRegion = regions.ToDictionary(r => r, r =>
                CombineActions(string.Join(',', actions.Concat(plan[r])), explicitLevers, _actions, a => DecidedLevers(a, r, latest)).Levers);
            var s = engine.Scenario(perRegion, dieselChange, durationWeeks);
            var uniform = perRegion.Values.Distinct().Count() == 1;
            return new
            {
                Scope = scopedRegion ?? $"Network ({string.Join(", ", s.Regions)})",
                DieselChangeUsdPerGallon = s.DieselShiftUsdPerGallon,
                s.DurationWeeks,
                Actions = actions,
                DecidedPlan = applyDecidedPlan ? plan.ToDictionary(kv => kv.Key, kv => DescribeSettings(kv.Value, kv.Key, latest, includeFixed: true)) : null,
                ActionSettings = regions.SelectMany(r => DescribeSettings(actions, r, latest).Select(d => regions.Length > 1 ? $"{r} {d}" : d)).ToArray(),
                Levers = DescribeLevers(s.Levers),
                LeversByRegion = uniform ? null : perRegion.ToDictionary(kv => kv.Key, kv => DescribeLevers(kv.Value)),
                FuelShockQuarterCostUsd = Math.Round(s.FuelShockQuarterCost),
                ScenarioQuarterCostUsd = Math.Round(s.ScenarioQuarterCost),
                NetOperatingIncomeImpactUsd = Math.Round(s.NetOperatingIncomeImpact),
                OffsetOfFuelShockPct = s.OffsetShare is { } offset ? Pct(offset) : (double?)null,
                WeeklyCostUsd = new
                {
                    Baseline = Math.Round(s.BaselineWeeklyCost),
                    WithFuelShock = Math.Round(s.ShockWeeklyCost),
                    WithLevers = Math.Round(s.LeversWeeklyCost),
                    WithFuelShockAndLevers = Math.Round(s.CombinedWeeklyCost),
                },
                ExpressWithin3HoursPct = Math.Round(s.ExpressWithin3HoursPct, 2),
                PromiseKeptPct = Math.Round(s.PromiseKeptPct, 2),
                AvailabilityPct = Math.Round(s.AvailabilityPct, 2),
                Guardrail = DescribeGuardrail(CheckAll(perRegion.Values)),
                Method = "Volumes from the latest complete week. The diesel change lasts duration_weeks; levers apply for all 13 weeks. " +
                         "Offset is the share of the quarter's added fuel cost recovered by the levers. Each region uses its own decided " +
                         "settings and plan; the guardrail must hold in every region. Service levels are modeled estimates.",
                Notice = DataNotice,
            };
        }, scopedRegionHint: region);

    public object CheckGuardrail(string? actionIds, Levers explicitLevers) => CheckGuardrail(actionIds, null, explicitLevers);

    public object CheckGuardrail(string? actionIds, string? region, Levers explicitLevers) =>
        Guarded(() =>
        {
            var scopedRegion = ScopedRegion(region);
            var regions = scopedRegion is null ? data.Regions.ToArray() : [scopedRegion];
            var latest = LatestDecisions();
            var (actions, _) = CombineActions(actionIds, Levers.None, _actions);
            var perRegion = regions.Select(r => CombineActions(actionIds, explicitLevers, _actions, a => DecidedLevers(a, r, latest)).Levers).ToArray();
            var combined = perRegion.Aggregate(Levers.None, (a, l) => new Levers
            {
                Batch = Math.Max(a.Batch, l.Batch),
                Closer = Math.Max(a.Closer, l.Closer),
                Split = Math.Max(a.Split, l.Split),
                Pickup = Math.Max(a.Pickup, l.Pickup),
                Fee = Math.Max(a.Fee, l.Fee),
                Basket = a.Basket || l.Basket,
                Cap = a.Cap || l.Cap,
            });
            return new
            {
                Actions = actions,
                ActionSettings = regions.SelectMany(r => DescribeSettings(actions, r, latest).Select(d => regions.Length > 1 ? $"{r} {d}" : d)).ToArray(),
                Levers = DescribeLevers(combined),
                Guardrail = DescribeGuardrail(CheckAll(perRegion)),
                Note = "Express-within-3-hours is a modeled estimate, not delivery telemetry.",
            };
        }, scopedRegionHint: region);

    /// <summary>The guardrail must hold in every region: the lowest Express-within-3-hours share decides.</summary>
    private static GuardrailCheck CheckAll(IEnumerable<Levers> perRegion)
    {
        var checks = perRegion.Select(CostEngine.CheckGuardrail).ToArray();
        var worst = checks.MinBy(c => c.ExpressWithin3HoursPct)!;
        return worst with
        {
            Passed = checks.All(c => c.Passed),
            Findings = checks.SelectMany(c => c.Findings).DistinctBy(f => f.Code).ToArray(),
        };
    }

    /// <summary>
    /// Merges playbook actions and explicit levers: the larger value of each lever wins.
    /// <paramref name="leversFor"/> supplies each action's levers (default: the playbook setting).
    /// </summary>
    public static (string[] Actions, Levers Levers) CombineActions(
        string? actionIds, Levers explicitLevers, ActionCatalog? catalog = null, Func<DemoAction, Levers>? leversFor = null)
    {
        catalog ??= ActionCatalog.Default;
        leversFor ??= a => a.Levers;
        var ids = (actionIds ?? string.Empty).Split([',', ';', ' ', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var combined = explicitLevers;
        var resolved = new List<string>();
        foreach (var id in ids)
        {
            var action = catalog.Find(id) ?? throw new ArgumentException($"Unknown action '{id}'. Use playbook action IDs A1 to A6.");
            if (resolved.Contains(action.Id))
            {
                continue;
            }

            resolved.Add(action.Id);
            var a = leversFor(action);
            combined = new Levers
            {
                Batch = Math.Max(combined.Batch, a.Batch),
                Closer = Math.Max(combined.Closer, a.Closer),
                Split = Math.Max(combined.Split, a.Split),
                Pickup = Math.Max(combined.Pickup, a.Pickup),
                // Only actions that set a fee affect it, so a what-if fee cut (negative) is not reset to 0.
                Fee = a.Fee != 0 ? Math.Max(combined.Fee, a.Fee) : combined.Fee,
                Basket = combined.Basket || a.Basket,
                Cap = combined.Cap || a.Cap,
            };
        }

        return (resolved.ToArray(), combined);
    }

    public object GetActionPlaybook(string? region) =>
        Guarded(() =>
        {
            var scopedRegion = ScopedRegion(region);
            var savingsRegions = scopedRegion is null ? data.Regions.ToArray() : [scopedRegion];
            var visible = data.Regions.Where(caller.CanSee).ToArray();
            var latest = LatestDecisions();
            return new
            {
                DecisionRegion = scopedRegion,
                SavingsScope = scopedRegion ?? "Network",
                SavingsNote = "Each saving is for that action alone, at current-week volumes, using each region's decided setting. " +
                              "Savings are not additive; use run_scenario to model actions together. Decisions are per region: " +
                              (scopedRegion is null ? "see ByRegion." : $"Status and AvailableDecisions are for {scopedRegion}."),
                Actions = data.Playbook.Select(p =>
                {
                    var modeled = _actions.Find(p.ActionId);
                    var byRegion = visible.Select(r => RegionDecision(p.ActionId, modeled, r, latest)).ToArray();
                    var focus = scopedRegion is null ? null : byRegion.Single(b => b.Region == scopedRegion);
                    var levers = focus?.Levers ?? modeled?.Levers;
                    var check = levers is null ? null : CostEngine.CheckGuardrail(levers);
                    return new
                    {
                        p.ActionId,
                        p.Action,
                        p.Effect,
                        p.CustomerImpact,
                        p.Approver,
                        GuardrailRule = p.Guardrail,
                        ModeledLevers = levers is null ? null : DescribeLevers(levers),
                        Levers = levers,
                        LeverSource = modeled?.Source,
                        LeverMapping = modeled is null ? "not modeled"
                            : modeled.LeversDerivedFromPlaybookText ? "derived from playbook text (approximation)" : "reference demo",
                        EstimatedWeeklySavingUsd = modeled is null
                            ? (double?)null
                            : Math.Round(savingsRegions.Sum(r => engine.WeeklySaving(DecidedLevers(modeled, r, latest), [r]))),
                        GuardrailPassed = check?.Passed,
                        GuardrailFindings = check?.Findings.Select(f => f.Code).ToArray() ?? [],
                        Region = focus?.Region,
                        Status = focus?.Status,
                        Setting = focus?.Setting,
                        CanAmendSetting = focus?.CanAmendSetting ?? false,
                        LatestDecision = focus?.LatestDecision,
                        LatestDecisionBy = focus?.LatestDecisionBy,
                        LatestDecisionAt = focus?.LatestDecisionAt,
                        LatestDecisionReason = focus?.LatestDecisionReason,
                        AvailableDecisions = focus?.AvailableDecisions ?? [],
                        ByRegion = byRegion,
                    };
                }).ToArray(),
                Notice = DataNotice,
            };
        }, scopedRegionHint: region);

    /// <summary>One action's decision state in one region, for the playbook.</summary>
    public sealed record RegionDecisionView(
        string Region,
        string Status,
        IReadOnlyList<string> AvailableDecisions,
        object? Setting,
        bool CanAmendSetting,
        string LatestDecision,
        string? LatestDecisionBy,
        string? LatestDecisionAt,
        string? LatestDecisionReason,
        [property: System.Text.Json.Serialization.JsonIgnore] Levers? Levers);

    private RegionDecisionView RegionDecision(string actionId, DemoAction? modeled, string region, IReadOnlyDictionary<(string, string), DecisionEntry> latest)
    {
        var d = Latest(latest, region, actionId);
        var spec = modeled?.Setting;
        var current = CurrentSetting(modeled, d);
        return new RegionDecisionView(
            region,
            DecisionWorkflow.Status(d?.Decision),
            caller.CanSee(region) ? DecisionWorkflow.AvailableTo(caller, d?.Decision).Select(k => k.Verb()).ToArray() : [],
            spec is null || current is null ? null : new
            {
                spec.Lever,
                spec.Label,
                Value = current.Value,
                spec.Default,
                spec.Min,
                spec.Max,
                spec.Step,
                IsDefault = current.Value == spec.Default,
                Text = spec.Format(current.Value),
            },
            spec is not null && DecisionWorkflow.CanAmend(caller.Role, d?.Decision),
            d is not null ? $"{d.Decision.Verb()} by {d.Actor} at {d.Timestamp:yyyy-MM-dd HH:mm}Z" : "none",
            d?.Actor,
            d?.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            string.IsNullOrEmpty(d?.Reason) ? null : d.Reason,
            modeled is null ? null : DecidedLevers(modeled, region, latest));
    }

    public object GetExecutionResults() =>
        Guarded(() =>
        {
            var region = data.CanonicalRegion(PilotModel.PilotRegion);
            EnsureCanSee(region);
            var latest = LatestDecisions();
            var executed = latest.Where(kv => kv.Key.Region == region).Select(kv => kv.Value)
                .Where(d => d.Decision == DecisionKind.Execute)
                .OrderBy(d => d.ActionId, StringComparer.Ordinal)
                .ToArray();
            if (executed.Length == 0)
            {
                return (object)new
                {
                    Status = "none",
                    Message = "No actions have been executed yet. Results appear after regional ops executes an approved action.",
                    Notice = DataNotice,
                };
            }

            var (actionIds, levers) = CombineActions(string.Join(',', executed.Select(d => d.ActionId)), Levers.None, _actions, a => DecidedLevers(a, region, latest));
            var zones = data.ZonesIn(region);
            var week = data.WeeklyInputs.CurrentWeek;
            var inputs = zones.Select(z => data.GetZoneInputs(week, z.Zone, DeliveryType.Express)).ToArray();
            var r = PilotModel.Compare(inputs[..PilotModel.PilotZoneCount], inputs[PilotModel.PilotZoneCount..], levers);
            var check = CostEngine.CheckGuardrail(levers);
            return new
            {
                Status = "ok",
                Region = region,
                ExecutedActions = executed.Select(d => new
                {
                    d.ActionId,
                    ExecutedBy = d.Actor,
                    ExecutedAt = d.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    Setting = DescribeSettings([d.ActionId], region, latest).FirstOrDefault(),
                }),
                ModeledLevers = DescribeLevers(levers),
                PilotZones = zones.Take(PilotModel.PilotZoneCount).Select(z => z.Zone),
                ControlZones = zones.Skip(PilotModel.PilotZoneCount).Select(z => z.Zone),
                ExpressCostPerOrderUsd = new
                {
                    PilotBefore = Usd(r.PilotBefore),
                    PilotAfter = Usd(r.PilotAfter),
                    PilotChangePct = Pct(r.PilotChangePct),
                    ControlBefore = Usd(r.ControlBefore),
                    ControlAfter = Usd(r.ControlAfter),
                    ControlChangePct = Pct(r.ControlChangePct),
                },
                WeeklySavingIfScaledUsd = Math.Round(engine.WeeklySaving(levers, [region])),
                ExpressWithin3HoursPct = Math.Round(check.ExpressWithin3HoursPct, 2),
                GuardrailPassed = check.Passed,
                Method = "Modeled readout at this week's volumes: pilot zones run the executed actions, control zones do not " +
                         "(assumed flat apart from +0.2% drift, as in the reference demo). Illustrative, not measured.",
                Notice = DataNotice,
            };
        }, scopedRegionHint: PilotModel.PilotRegion);

    public Task<object> LogDecisionAsync(string actionId, string decision, string? reason, CancellationToken cancellationToken) =>
        LogDecisionAsync(actionId, decision, reason, null, null, cancellationToken);

    public Task<object> LogDecisionAsync(string actionId, string decision, string? reason, double? setting, CancellationToken cancellationToken) =>
        LogDecisionAsync(actionId, decision, reason, setting, null, cancellationToken);

    /// <param name="setting">
    /// Optional value for the action's adjustable lever (fraction) when proposing or approving. Null keeps the
    /// current setting: the proposed one when approving a proposal, otherwise the playbook setting. Proposing
    /// again (ops) or approving again (finance) with a different value changes the setting before execution.
    /// </param>
    /// <param name="region">
    /// The region the decision applies to. Regional ops always decide for their own region; finance must name one
    /// unless exactly one region has the action proposed.
    /// </param>
    public Task<object> LogDecisionAsync(
        string actionId, string decision, string? reason, double? setting, string? region, CancellationToken cancellationToken) =>
        GuardedAsync(async () =>
        {
            var playbook = data.Playbook.FirstOrDefault(p => string.Equals(p.ActionId, actionId?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown action '{actionId}'. Use an action ID from the playbook (A1 to A6).");
            if (!Enum.TryParse<DecisionKind>(decision?.Trim(), ignoreCase: true, out var kind) || !Enum.IsDefined(kind)
                || int.TryParse(decision, out _))
            {
                throw new ArgumentException("Decision must be propose, approve, reject or execute.");
            }

            var cleanReason = reason?.Trim() ?? string.Empty;
            switch (kind)
            {
                case DecisionKind.Approve or DecisionKind.Reject when !caller.CanApprove:
                    throw new ScopeDeniedException($"{caller.DisplayName} ({caller.Role}) cannot approve or reject. Fulfillment Finance (FinanceAnalyst) approves.");
                case DecisionKind.Propose or DecisionKind.Execute when caller.Role != DemoRole.RegionalOps:
                    throw new ScopeDeniedException($"{caller.DisplayName} ({caller.Role}) cannot {kind.ToString().ToLowerInvariant()} actions. Regional Delivery Ops (RegionalOps) proposes and executes.");
                case DecisionKind.Reject when cleanReason.Length == 0:
                    throw new ArgumentException("A reason is required to reject an action.");
                case DecisionKind.Reject or DecisionKind.Execute when setting is not null:
                    throw new ArgumentException("A setting can only be given when proposing or approving.");
            }

            var modeled = _actions.Find(playbook.ActionId);
            var spec = modeled?.Setting;
            if (setting is not null && spec is null)
            {
                throw new ArgumentException($"{playbook.ActionId} has no adjustable setting; it is applied as defined in the playbook.");
            }

            var all = LatestDecisions();
            var decisionRegion = ResolveDecisionRegion(region, playbook.ActionId, kind, all);
            var latest = Latest(all, decisionRegion, playbook.ActionId);
            var current = CurrentSetting(modeled, latest);
            var resolved = kind == DecisionKind.Reject || spec is null ? null : setting is { } requested ? Math.Round(requested, 4) : current;
            var levers = modeled?.WithSetting(resolved);
            var canAmend = spec is not null && latest is not null && kind == latest.Decision && DecisionWorkflow.CanAmend(caller.Role, latest.Decision);
            var isAmendment = canAmend && setting is not null && resolved != current;

            var next = DecisionWorkflow.NextSteps(latest?.Decision);
            if (!next.Contains(kind) && !isAmendment)
            {
                var status = DecisionWorkflow.Status(latest?.Decision);
                throw new DecisionConflictException(next.Count == 0
                    ? $"{playbook.ActionId} is already {status.ToLowerInvariant()} for {decisionRegion}; no further decisions until the demo is reset."
                    : canAmend
                        ? $"{playbook.ActionId} is already {status.ToLowerInvariant()} for {decisionRegion} at {spec!.Format(current!.Value)}. To change it, give a different setting."
                        : $"{playbook.ActionId} is {status.ToLowerInvariant()} for {decisionRegion}, so it cannot be {(kind == DecisionKind.Propose ? "proposed" : kind.Verb() + "d")} now. " +
                          $"Next steps: {string.Join(" or ", next.Select(k => k.Verb()))}.");
            }

            var check = levers is null ? null : CostEngine.CheckGuardrail(levers);
            if (kind is DecisionKind.Approve or DecisionKind.Execute && check is { Passed: false })
            {
                var at = resolved is { } r ? $" at {spec!.Format(r)}" : string.Empty;
                throw new ScopeDeniedException($"{playbook.ActionId}{at} breaks the Express 3-hour guardrail and cannot be {kind.ToString().ToLowerInvariant()}d for {decisionRegion}.");
            }

            var settingText = resolved is { } value
                ? spec!.Format(value) + (current is { } was && was != value ? $" (was {(was * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%)" : string.Empty)
                : null;
            var proposal = new DecisionProposal(
                playbook.ActionId,
                playbook.Action,
                kind,
                caller.DisplayName,
                cleanReason,
                levers is null ? null : Math.Round(engine.WeeklySaving(levers, [decisionRegion])),
                check?.Findings.Select(f => f.Message).ToArray() ?? [],
                settingText,
                decisionRegion);

            System.Diagnostics.Activity.Current?.SetTag(AgentTelemetry.DecisionTag, $"{playbook.ActionId}:{kind.ToString().ToLowerInvariant()}:{decisionRegion}");
            if (!await confirmer.ConfirmAsync(proposal, cancellationToken).ConfigureAwait(false))
            {
                return (object)new
                {
                    Status = "not_recorded",
                    Message = "The user declined in the confirmation prompt. Nothing was logged.",
                };
            }

            var entry = new DecisionEntry(_time.GetUtcNow(), playbook.ActionId, caller.DisplayName, kind, cleanReason, resolved, decisionRegion);
            decisionLog.Append(entry);
            return new
            {
                Status = "recorded",
                entry.ActionId,
                entry.Region,
                Decision = kind.ToString().ToLowerInvariant(),
                entry.Actor,
                Timestamp = entry.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                entry.Reason,
                Setting = settingText,
                SettingValue = resolved,
            };
        });

    /// <summary>
    /// The action's setting as currently decided in one region: the value logged with its latest propose,
    /// approve or execute step there, otherwise the playbook setting. Null when the action has no setting.
    /// </summary>
    private static double? CurrentSetting(DemoAction? action, DecisionEntry? latest) =>
        action?.Setting is not { } spec ? null
        : latest is { Decision: not DecisionKind.Reject, Setting: { } logged } && logged >= spec.Min && logged <= spec.Max ? logged
        : spec.Default;

    private static Levers DecidedLevers(DemoAction action, string region, IReadOnlyDictionary<(string, string), DecisionEntry> latest) =>
        action.WithSetting(CurrentSetting(action, Latest(latest, region, action.Id)));

    /// <summary>Approved or executed actions in a region: the plan applied to that region's outcomes.</summary>
    private static string[] PlanIds(string region, IReadOnlyDictionary<(string Region, string ActionId), DecisionEntry> latest) =>
        latest.Where(kv => kv.Key.Region == region && kv.Value.Decision is DecisionKind.Approve or DecisionKind.Execute)
            .Select(kv => kv.Key.ActionId).Order(StringComparer.Ordinal).ToArray();

    private string[] DescribeSettings(IEnumerable<string> actionIds, string region, IReadOnlyDictionary<(string, string), DecisionEntry> latest, bool includeFixed = false) =>
        actionIds.Select(id => _actions.Find(id)).OfType<DemoAction>()
            .Where(a => includeFixed || a.Setting is not null)
            .Select(a => a.Setting is { } spec
                ? $"{a.Id}: {spec.Format(CurrentSetting(a, Latest(latest, region, a.Id))!.Value)}"
                : a.Id)
            .ToArray();

    private static DecisionEntry? Latest(IReadOnlyDictionary<(string, string), DecisionEntry> latest, string region, string actionId) =>
        latest.GetValueOrDefault((region, actionId.Trim().ToUpperInvariant()));

    /// <summary>Latest decision per (region, action). Rows from before regions count as the legacy region.</summary>
    private Dictionary<(string Region, string ActionId), DecisionEntry> LatestDecisions() =>
        decisionLog.ReadAll()
            .GroupBy(d => (Region: data.CanonicalRegion(d.EffectiveRegion), ActionId: d.ActionId.Trim().ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Last());

    /// <summary>
    /// The region a decision applies to. Regional ops: their own region. Finance: the named region, or the one
    /// region where the action is proposed. Decisions never apply to the whole network at once.
    /// </summary>
    private string ResolveDecisionRegion(string? requested, string actionId, DecisionKind kind, IReadOnlyDictionary<(string, string), DecisionEntry> latest)
    {
        var wantsNetwork = string.IsNullOrWhiteSpace(requested)
            || requested.Trim().Equals("network", StringComparison.OrdinalIgnoreCase)
            || requested.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        if (!wantsNetwork)
        {
            var canonical = data.CanonicalRegion(requested!);
            EnsureCanSee(canonical);
            return canonical;
        }

        if (!caller.IsNetworkWide && caller.AllowedRegions.Count == 1)
        {
            return data.CanonicalRegion(caller.AllowedRegions[0]);
        }

        var proposedIn = data.Regions.Where(r => Latest(latest, r, actionId)?.Decision == DecisionKind.Propose).ToArray();
        if (kind is DecisionKind.Approve or DecisionKind.Reject && proposedIn.Length == 1)
        {
            return proposedIn[0];
        }

        throw new ArgumentException(
            $"Say which region this decision is for ({string.Join(", ", data.Regions)}). Decisions apply to one region at a time.");
    }

    /// <summary>Resolves a requested region against the caller's scope. Null means the whole network.</summary>
    private string? ScopedRegion(string? requested)
    {
        var wantsNetwork = string.IsNullOrWhiteSpace(requested)
            || requested.Trim().Equals("network", StringComparison.OrdinalIgnoreCase)
            || requested.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);

        if (wantsNetwork)
        {
            if (caller.IsNetworkWide)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(requested) && caller.AllowedRegions.Count == 1)
            {
                return data.CanonicalRegion(caller.AllowedRegions[0]);
            }

            throw new ScopeDeniedException($"{caller.DisplayName} ({caller.Role}) can only view {caller.ScopeDescription} data, not the whole network.");
        }

        var canonical = data.CanonicalRegion(requested!);
        EnsureCanSee(canonical);
        return canonical;
    }

    private void EnsureCanSee(string region)
    {
        if (!caller.CanSee(region))
        {
            throw new ScopeDeniedException($"{caller.DisplayName} ({caller.Role}) can only view {caller.ScopeDescription} data, not {region}.");
        }
    }

    private object Guarded(Func<object> body, string? scopedRegionHint, [CallerMemberName] string member = "")
    {
        var scope = string.IsNullOrWhiteSpace(scopedRegionHint) ? (caller.IsNetworkWide ? "network" : caller.ScopeDescription) : scopedRegionHint;
        try
        {
            return Completed(member, scope, "ok", body());
        }
        catch (ScopeDeniedException ex)
        {
            return Completed(member, scope, "scope_denied", new { Error = "scope_denied", ex.Message });
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Completed(member, scope, "invalid_request", new { Error = "invalid_request", Message = FirstLine(ex.Message) });
        }
    }

    private async Task<object> GuardedAsync(Func<Task<object>> body, [CallerMemberName] string member = "")
    {
        try
        {
            return Completed(member, caller.ScopeDescription, "ok", await body().ConfigureAwait(false));
        }
        catch (ScopeDeniedException ex)
        {
            return Completed(member, caller.ScopeDescription, "scope_denied", new { Error = "scope_denied", ex.Message });
        }
        catch (DecisionConflictException ex)
        {
            return Completed(member, caller.ScopeDescription, "conflict", new { Error = "conflict", ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Completed(member, caller.ScopeDescription, "invalid_request", new { Error = "invalid_request", Message = FirstLine(ex.Message) });
        }
    }

    private object Completed(string member, string scope, string outcome, object result)
    {
        AgentTelemetry.TagTool(caller.Role.ToString(), scope, outcome);
        onToolCompleted?.Invoke(ToolNames.GetValueOrDefault(member, member), outcome, result);
        return result;
    }

    private static DateOnly? ParseDate(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", out var date)
                ? date
                : throw new ArgumentException($"{name} must be a yyyy-MM-dd date.");

    private static object DescribeLevers(Levers l) => new
    {
        BatchExpressShare = l.Batch,
        CloserStoreShare = l.Closer,
        SplitShipmentReduction = l.Split,
        PickupShiftShare = l.Pickup,
        ExpressFeeChangeUsd = l.Fee,
        RuralMinimumBasket = l.Basket,
        CarrierSurchargeCap = l.Cap,
    };

    private static object DescribeGuardrail(GuardrailCheck g) => new
    {
        g.Passed,
        ExpressWithin3HoursPct = Math.Round(g.ExpressWithin3HoursPct, 2),
        g.ThresholdPct,
        Findings = g.Findings.Select(f => new { f.Code, Severity = f.Severity.ToString(), f.Message }).ToArray(),
    };

    private static object? Pair(double? prior, double? current, int digits) =>
        prior is null || current is null ? null : new { Prior = Math.Round(prior.Value, digits), Current = Math.Round(current.Value, digits) };

    private static double Usd(double value) => Math.Round(value, 2);

    private static double Pct(double value) => Math.Round(value * 100, 1);

    private static double? Pct(double? value) => value is null ? null : Pct(value.Value);

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();
}
