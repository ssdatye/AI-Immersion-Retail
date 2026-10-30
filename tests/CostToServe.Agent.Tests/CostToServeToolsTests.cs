using System.Text.Json;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Model;
using CostToServe.Agent.Security;
using CostToServe.Agent.Tools;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;
using CostToServe.Core.State;
using Microsoft.Extensions.AI;

namespace CostToServe.Agent.Tests;

/// <summary>Tool behavior, scope enforcement and decision rules, without calling a model.</summary>
public sealed class CostToServeToolsTests : IDisposable
{
    private static readonly SeedDataSet Seed = SeedDataSet.Load(SeedDirectory());
    private static readonly CostEngine Engine = new(Seed.WeeklyInputs);
    private readonly InMemoryDecisionLog _log = new();
    private readonly StubConfirmer _confirmer = new();
    private readonly string _temp = Directory.CreateTempSubdirectory("cts-agent-tests-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private CostToServeTools ToolsFor(CallerContext caller) => new(Seed, Engine, caller, _log, _confirmer);

    [Fact]
    public void Exposes_eight_tools_and_never_takes_identity_from_the_model()
    {
        var tools = ToolsFor(DemoPersonas.Dana).Create().Cast<AIFunction>().ToArray();

        Assert.Equal(
            ["check_guardrail", "explain_variance", "get_action_playbook", "get_cost_per_order", "get_execution_results", "log_decision", "propose_package", "run_scenario"],
            tools.Select(t => t.Name).Order());
        foreach (var tool in tools)
        {
            var parameters = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
            Assert.DoesNotContain(parameters, p => p.Contains("actor") || p.Contains("role") || p.Contains("user")
                || p.Contains("caller") || p.Contains("approver") || p.Contains("scope"));
        }
    }

    [Fact]
    public async Task Model_facing_run_scenario_combines_A1_and_A2_to_offset_88_percent()
    {
        var runScenario = ToolsFor(DemoPersonas.Dana).Create().Cast<AIFunction>().Single(t => t.Name == "run_scenario");

        var result = Json(await runScenario.InvokeAsync(new AIFunctionArguments
        {
            ["diesel_change_usd_per_gallon"] = 0.5,
            ["duration_weeks"] = 13,
            ["action_ids"] = "A1,A2",
        }));

        Assert.StartsWith("Network", result.GetProperty("scope").GetString());
        Assert.Equal(88.1, result.GetProperty("offsetOfFuelShockPct").GetDouble());
        Assert.Equal(28.2, Math.Round(result.GetProperty("fuelShockQuarterCostUsd").GetDouble() / 1e6, 1));
        Assert.True(result.GetProperty("guardrail").GetProperty("passed").GetBoolean());
        Assert.Equal(84.25, result.GetProperty("expressWithin3HoursPct").GetDouble());
    }

    [Fact]
    public void Tool_numbers_match_the_engine()
    {
        var result = Json(ToolsFor(DemoPersonas.Dana).ExplainVariance("Southeast", "Express"));
        var v = Engine.ExplainVariance("Southeast", DeliveryType.Express);

        Assert.Equal(Math.Round(v.PriorCostPerOrder, 2), result.GetProperty("priorCostPerOrderUsd").GetDouble());
        Assert.Equal(Math.Round(v.CurrentCostPerOrder, 2), result.GetProperty("currentCostPerOrderUsd").GetDouble());
        Assert.Equal(15.56, result.GetProperty("priorCostPerOrderUsd").GetDouble());
        Assert.Equal(17.01, result.GetProperty("currentCostPerOrderUsd").GetDouble());
        Assert.Equal(9.3, result.GetProperty("changePct").GetDouble());
    }

    [Theory]
    [InlineData("Midwest", null)]
    [InlineData("network", null)]
    [InlineData(null, "Midwest Zone 1")]
    public void Regional_ops_cannot_read_outside_their_region(string? region, string? zone)
    {
        var result = Json(ToolsFor(DemoPersonas.Luis).GetCostPerOrder(region, zone, "Express", null, null));

        Assert.Equal("scope_denied", result.GetProperty("error").GetString());
    }

    [Fact]
    public void Regional_ops_default_to_their_region_everywhere()
    {
        var luis = ToolsFor(DemoPersonas.Luis);

        Assert.Equal("Southeast", Json(luis.GetCostPerOrder(null, null, "Express", null, null)).GetProperty("scope").GetString());
        Assert.Equal("Southeast", Json(luis.RunScenario(0.5, 13, null, null, Levers.None)).GetProperty("scope").GetString());
        Assert.Equal("Southeast", Json(luis.GetActionPlaybook(null)).GetProperty("savingsScope").GetString());
        Assert.Equal("scope_denied", Json(luis.RunScenario(0.5, 13, "West", null, Levers.None)).GetProperty("error").GetString());
        Assert.Equal("scope_denied", Json(luis.ExplainVariance("Midwest", "Express")).GetProperty("error").GetString());
    }

    [Fact]
    public void Southeast_shock_is_smaller_than_the_network_shock()
    {
        var southeast = Json(ToolsFor(DemoPersonas.Luis).RunScenario(0.5, 13, null, null, Levers.None));
        var network = Json(ToolsFor(DemoPersonas.Dana).RunScenario(0.5, 13, null, null, Levers.None));

        Assert.True(southeast.GetProperty("fuelShockQuarterCostUsd").GetDouble() < network.GetProperty("fuelShockQuarterCostUsd").GetDouble());
    }

    [Fact]
    public void Invalid_input_returns_an_error_instead_of_throwing()
    {
        var dana = ToolsFor(DemoPersonas.Dana);

        Assert.Equal("invalid_request", Json(dana.RunScenario(0.5, 13, null, null, new Levers { Batch = 0.9 })).GetProperty("error").GetString());
        Assert.Equal("invalid_request", Json(dana.RunScenario(0.5, 13, null, "A9", Levers.None)).GetProperty("error").GetString());
        Assert.Equal("invalid_request", Json(dana.GetCostPerOrder("Southeast", null, "Drone", null, null)).GetProperty("error").GetString());
        Assert.Equal("invalid_request", Json(dana.GetCostPerOrder("Southeast", null, null, "10/19/2026", null)).GetProperty("error").GetString());
    }

    [Fact]
    public void Combining_actions_takes_the_larger_value_of_each_lever()
    {
        var (actions, levers) = CostToServeTools.CombineActions("a1 + A2, A1", new Levers { Batch = 0.5 });

        Assert.Equal(["A1", "A2"], actions);
        Assert.Equal(0.5, levers.Batch);
        Assert.Equal(0.2, levers.Closer);
    }

    [Fact]
    public void Combining_actions_keeps_a_what_if_fee_cut()
    {
        var (_, levers) = CostToServeTools.CombineActions("A1,A5", new Levers { Fee = -0.5 });

        Assert.Equal(-0.5, levers.Fee);
        Assert.True(levers.Cap);
    }

    [Fact]
    public async Task Decisions_follow_separation_of_duties()
    {
        var dana = ToolsFor(DemoPersonas.Dana);
        var luis = ToolsFor(DemoPersonas.Luis);

        Assert.Equal("scope_denied", await Error(luis.LogDecisionAsync("A1", "approve", null, default)));
        Assert.Equal("scope_denied", await Error(dana.LogDecisionAsync("A1", "propose", null, null, "Southeast", default)));
        Assert.Equal("conflict", await Error(luis.LogDecisionAsync("A1", "execute", null, default)));
        Assert.Equal("invalid_request", await Error(dana.LogDecisionAsync("A1", "reject", "  ", null, "Southeast", default)));
        Assert.Equal("invalid_request", await Error(dana.LogDecisionAsync("A9", "approve", null, null, "Southeast", default)));
        Assert.Equal("invalid_request", await Error(dana.LogDecisionAsync("A1", "1", null, null, "Southeast", default)));
        Assert.Empty(_log.Entries);
        Assert.Equal(0, _confirmer.Calls);

        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A1", "propose", null, default)));
        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A1", "approve", "Pilot first", null, "Southeast", default)));
        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A1", "execute", null, default)));

        Assert.Equal(
            [("Luis", DecisionKind.Propose), ("Dana", DecisionKind.Approve), ("Luis", DecisionKind.Execute)],
            _log.Entries.Select(e => (e.Actor, e.Decision)));
        Assert.Equal("Pilot first", _log.Entries[1].Reason);
    }

    [Fact]
    public async Task Workflow_blocks_duplicate_and_out_of_order_steps()
    {
        var dana = ToolsFor(DemoPersonas.Dana);
        var luis = ToolsFor(DemoPersonas.Luis);

        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A2", "approve", null, null, "Southeast", default)));
        Assert.Equal("conflict", await Error(dana.LogDecisionAsync("A2", "approve", null, null, "Southeast", default)));
        // Proposing an approved action is a revision, which needs a new setting.
        Assert.Equal("invalid_request", await Error(luis.LogDecisionAsync("A2", "propose", null, default)));
        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A2", "reject", "Revoked: stock risk", null, "Southeast", default)));
        Assert.Equal("conflict", await Error(luis.LogDecisionAsync("A2", "execute", null, default)));
        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A2", "propose", null, default)));
        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A2", "approve", null, null, "Southeast", default)));
        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A2", "execute", null, default)));
        Assert.Equal("conflict", await Error(luis.LogDecisionAsync("A2", "execute", null, default)));
        Assert.Equal("conflict", await Error(dana.LogDecisionAsync("A2", "reject", "too late", null, "Southeast", default)));
        Assert.Equal(5, _log.Entries.Count);
    }

    [Fact]
    public async Task Playbook_shows_status_and_the_callers_next_steps()
    {
        var dana = ToolsFor(DemoPersonas.Dana);
        var luis = ToolsFor(DemoPersonas.Luis);
        await luis.LogDecisionAsync("A1", "propose", null, default);

        var a1ForDana = Action(dana, "A1");
        var a1ForLuis = Action(luis, "A1");
        var a2ForLuis = Action(luis, "A2");

        Assert.Equal("Proposed", a1ForDana.GetProperty("status").GetString());
        Assert.Equal("Luis", a1ForDana.GetProperty("latestDecisionBy").GetString());
        Assert.Equal(["approve", "reject"], a1ForDana.GetProperty("availableDecisions").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(a1ForLuis.GetProperty("availableDecisions").EnumerateArray());
        Assert.Equal("Not reviewed", a2ForLuis.GetProperty("status").GetString());
        Assert.Equal(["propose"], a2ForLuis.GetProperty("availableDecisions").EnumerateArray().Select(x => x.GetString()));

        static JsonElement Action(CostToServeTools tools, string id) =>
            Json(tools.GetActionPlaybook("Southeast")).GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("actionId").GetString() == id);
    }

    [Fact]
    public async Task Execution_results_appear_only_after_execution_and_use_the_executed_levers()
    {
        var dana = ToolsFor(DemoPersonas.Dana);
        var luis = ToolsFor(DemoPersonas.Luis);

        Assert.Equal("none", Json(luis.GetExecutionResults()).GetProperty("status").GetString());

        await dana.LogDecisionAsync("A1", "approve", null, null, "Southeast", default);
        await dana.LogDecisionAsync("A2", "approve", null, null, "Southeast", default);
        Assert.Equal("none", Json(luis.GetExecutionResults()).GetProperty("status").GetString());

        await luis.LogDecisionAsync("A1", "execute", null, default);
        await luis.LogDecisionAsync("A2", "execute", null, default);
        var result = Json(luis.GetExecutionResults());
        var cpo = result.GetProperty("expressCostPerOrderUsd");

        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal("Southeast", result.GetProperty("region").GetString());
        Assert.Equal(["A1", "A2"], result.GetProperty("executedActions").EnumerateArray().Select(a => a.GetProperty("actionId").GetString()));
        Assert.True(cpo.GetProperty("pilotAfter").GetDouble() < cpo.GetProperty("pilotBefore").GetDouble());
        Assert.Equal(0.2, cpo.GetProperty("controlChangePct").GetDouble());
        Assert.Equal(Math.Round(Engine.WeeklySaving(new Levers { Batch = 0.35, Closer = 0.2 }, ["Southeast"])), result.GetProperty("weeklySavingIfScaledUsd").GetDouble());
        Assert.True(result.GetProperty("guardrailPassed").GetBoolean());
    }

    [Fact]
    public async Task Settings_follow_the_decision_from_proposal_to_execution()
    {
        var dana = ToolsFor(DemoPersonas.Dana);
        var luis = ToolsFor(DemoPersonas.Luis);

        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A6", "propose", null, 0.05, default)));
        var proposed = PlaybookAction(dana, "A6");
        Assert.Equal(0.05, proposed.GetProperty("setting").GetProperty("value").GetDouble());
        Assert.False(proposed.GetProperty("setting").GetProperty("isDefault").GetBoolean());
        Assert.Equal(Math.Round(Engine.WeeklySaving(new Levers { Pickup = 0.05 }, ["Southeast"])), proposed.GetProperty("estimatedWeeklySavingUsd").GetDouble());

        // Approving without a setting approves the proposed one.
        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A6", "approve", null, null, "Southeast", default)));
        Assert.Equal(0.05, _log.Entries[^1].Setting);

        // Finance changes the approval; repeating the same value is a conflict. Ops can only propose a revision.
        Assert.True(PlaybookAction(dana, "A6").GetProperty("canAmendSetting").GetBoolean());
        Assert.False(PlaybookAction(luis, "A6").GetProperty("canAmendSetting").GetBoolean());
        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A6", "approve", "smaller pilot", 0.04, "Southeast", default)));
        Assert.Equal("conflict", await Error(dana.LogDecisionAsync("A6", "approve", null, 0.04, "Southeast", default)));
        Assert.Equal("conflict", await Error(dana.LogDecisionAsync("A6", "approve", null, null, "Southeast", default)));
        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A6", "propose", null, 0.06, default)));

        // The approved 4% stays in force while the revision waits; executing runs it and the revision lapses.
        var scenario = Json(dana.RunScenario(0.5, 13, "Southeast", "A6", Levers.None));
        Assert.Equal(0.04, scenario.GetProperty("levers").GetProperty("pickupShiftShare").GetDouble());
        Assert.Equal(["A6: Shift to Pickup 4%"], scenario.GetProperty("actionSettings").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A6", "execute", null, default)));
        Assert.Equal(0.04, _log.Entries[^1].Setting);
        Assert.Equal(Math.Round(Engine.WeeklySaving(new Levers { Pickup = 0.04 }, ["Southeast"])),
            Json(luis.GetExecutionResults()).GetProperty("weeklySavingIfScaledUsd").GetDouble());
    }

    [Fact]
    public async Task Ops_can_revise_a_pending_proposal_and_a_rejection_resets_to_the_playbook_setting()
    {
        var dana = ToolsFor(DemoPersonas.Dana);
        var luis = ToolsFor(DemoPersonas.Luis);

        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A4", "propose", null, 0.3, default)));
        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A4", "propose", null, 0.5, default)));
        Assert.Equal("conflict", await Error(luis.LogDecisionAsync("A4", "propose", null, 0.5, default)));
        Assert.Equal(0.5, PlaybookAction(dana, "A4").GetProperty("setting").GetProperty("value").GetDouble());

        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A4", "reject", "too aggressive", null, "Southeast", default)));
        Assert.Null(_log.Entries[^1].Setting);
        Assert.Equal(0.4, PlaybookAction(dana, "A4").GetProperty("setting").GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Settings_are_validated_and_guardrail_checked()
    {
        var dana = ToolsFor(DemoPersonas.Dana);

        // 50% batching drops Express within 3 hours below 80%.
        Assert.Equal("scope_denied", await Error(dana.LogDecisionAsync("A1", "approve", null, 0.5, "Southeast", default)));
        Assert.Equal("invalid_request", await Error(dana.LogDecisionAsync("A6", "approve", null, 0.5, "Southeast", default)));
        Assert.Equal("invalid_request", await Error(dana.LogDecisionAsync("A3", "approve", null, 0.1, "Southeast", default)));
        Assert.Equal("invalid_request", await Error(dana.LogDecisionAsync("A6", "reject", "no", 0.05, "Southeast", default)));
        Assert.Empty(_log.Entries);

        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A1", "approve", null, 0.4, "Southeast", default)));
        Assert.Equal(0.4, _log.Entries[^1].Setting);
    }

    private static JsonElement PlaybookAction(CostToServeTools tools, string id) =>
        Json(tools.GetActionPlaybook("Southeast")).GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("actionId").GetString() == id);

    [Fact]
    public async Task Decisions_apply_to_one_region_only()
    {
        var dana = ToolsFor(DemoPersonas.Dana);
        var luis = ToolsFor(DemoPersonas.Luis);

        // Luis always decides for the Southeast and cannot act elsewhere.
        Assert.Equal("recorded", await Status(luis.LogDecisionAsync("A2", "propose", null, null, null, default)));
        Assert.Equal("Southeast", _log.Entries[^1].Region);
        Assert.Equal("scope_denied", await Error(luis.LogDecisionAsync("A4", "propose", null, null, "West", default)));

        // Finance approves the only pending proposal without naming the region; otherwise a region is required.
        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A2", "approve", null, default)));
        Assert.Equal("Southeast", _log.Entries[^1].Region);
        Assert.Equal("invalid_request", await Error(dana.LogDecisionAsync("A1", "approve", null, default)));
        Assert.Equal("recorded", await Status(dana.LogDecisionAsync("A1", "approve", null, null, "midwest", default)));
        Assert.Equal("Midwest", _log.Entries[^1].Region);

        var a2 = Json(dana.GetActionPlaybook(null)).GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("actionId").GetString() == "A2");
        Assert.False(a2.TryGetProperty("status", out _));
        Assert.Equal(["Approved", "Not reviewed", "Not reviewed"],
            a2.GetProperty("byRegion").EnumerateArray().Select(r => r.GetProperty("status").GetString()));
        Assert.Equal(["Southeast"],
            Json(luis.GetActionPlaybook(null)).GetProperty("actions")[0].GetProperty("byRegion").EnumerateArray().Select(r => r.GetProperty("region").GetString()));

        // The plan applies A2 to the Southeast and A1 to the Midwest only.
        var withPlan = Json(dana.RunScenario(0.5, 13, null, null, applyDecidedPlan: true, Levers.None));
        var expected = Engine.Scenario(new Dictionary<string, Levers>
        {
            ["Southeast"] = new() { Closer = 0.2 },
            ["Midwest"] = new() { Batch = 0.35 },
            ["West"] = Levers.None,
        }, 0.5, 13);
        Assert.Equal(Math.Round(expected.ScenarioQuarterCost), withPlan.GetProperty("scenarioQuarterCostUsd").GetDouble());
        Assert.Equal(["A2"], withPlan.GetProperty("decidedPlan").GetProperty("Southeast").EnumerateArray().Select(x => x.GetString()![..2]));
        Assert.Empty(withPlan.GetProperty("decidedPlan").GetProperty("West").EnumerateArray());
        Assert.True(withPlan.GetProperty("scenarioQuarterCostUsd").GetDouble()
            > Json(dana.RunScenario(0.5, 13, null, "A1,A2", Levers.None)).GetProperty("scenarioQuarterCostUsd").GetDouble());

        // Luis's scenario uses only the Southeast plan.
        var luisPlan = Json(luis.RunScenario(0.5, 13, null, null, applyDecidedPlan: true, Levers.None));
        Assert.Equal(Math.Round(Engine.Scenario(new Levers { Closer = 0.2 }, 0.5, 13, ["Southeast"]).ScenarioQuarterCost),
            luisPlan.GetProperty("scenarioQuarterCostUsd").GetDouble());
    }

    [Fact]
    public async Task Declined_confirmation_logs_nothing()
    {
        _confirmer.Answer = false;

        var status = await Status(ToolsFor(DemoPersonas.Dana).LogDecisionAsync("A2", "approve", null, null, "Southeast", default));

        Assert.Equal("not_recorded", status);
        Assert.Equal(1, _confirmer.Calls);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Csv_decision_log_round_trips_quoted_reasons()
    {
        var stateDir = Path.Combine(_temp, "local");
        new LocalDemoState(SeedDirectory(), stateDir).EnsureInitialized();
        var log = new CsvDecisionLog(stateDir);
        var entry = new DecisionEntry(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero), "A3", "Dana", DecisionKind.Reject,
            "Customer risk, \"urban\" zips\nexcluded");

        log.Append(entry);
        var read = Assert.Single(log.ReadAll());

        Assert.Equal(entry with { Reason = "Customer risk, \"urban\" zips excluded" }, read);
        Assert.Equal(2, File.ReadAllLines(log.Path).Length);
    }

    [Fact]
    public void Csv_decision_log_adds_the_setting_column_and_keeps_older_rows()
    {
        var stateDir = Path.Combine(_temp, "local");
        new LocalDemoState(SeedDirectory(), stateDir).EnsureInitialized();
        var log = new CsvDecisionLog(stateDir);
        File.AppendAllText(log.Path, "2026-10-26T09:00:00Z,A2,Luis,propose,,\n");

        log.Append(new DecisionEntry(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero), "A6", "Dana", DecisionKind.Approve, "", 0.045));
        var lines = File.ReadAllLines(log.Path);
        var read = log.ReadAll();

        Assert.EndsWith(",setting,region", lines[0]);
        Assert.Equal([null, 0.045], read.Select(e => e.Setting));
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Instructions_state_the_caller_scope_and_the_numbers_rule()
    {
        var instructions = CostToServeAgentFactory.BuildInstructions(DemoPersonas.Luis, Seed);

        Assert.Contains("Data scope: Southeast", instructions);
        Assert.Contains("Every number you state must come from a tool result", instructions);
        Assert.Contains("2026-10-19 to 2026-10-25", instructions);
    }

    private static JsonElement Json(object? value) => value is JsonElement e ? e : JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions);

    private static async Task<string?> Error(Task<object> result) => Json(await result).GetProperty("error").GetString();

    private static async Task<string?> Status(Task<object> result) => Json(await result).GetProperty("status").GetString();

    private static string SeedDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CostToServe.slnx")))
            {
                return Path.Combine(dir.FullName, "data", "seed");
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class InMemoryDecisionLog : IDecisionLog
    {
        public List<DecisionEntry> Entries { get; } = [];

        public IReadOnlyList<DecisionEntry> ReadAll() => Entries;

        public void Append(DecisionEntry entry) => Entries.Add(entry);
    }

    private sealed class StubConfirmer : IDecisionConfirmer
    {
        public bool Answer { get; set; } = true;

        public int Calls { get; private set; }

        public Task<bool> ConfirmAsync(DecisionProposal proposal, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }
}
