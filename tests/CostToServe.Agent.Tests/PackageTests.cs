using System.Text.Json;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Security;
using CostToServe.Agent.Tools;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;
using Microsoft.Extensions.AI;

namespace CostToServe.Agent.Tests;

/// <summary>Custom packages: regional ops bundles lever settings into one action that Finance decides as a whole.</summary>
public sealed class PackageTests
{
    private static readonly SeedDataSet Seed = SeedDataSet.Load(SeedDirectory());
    private static readonly CostEngine Engine = new(Seed.WeeklyInputs);
    private static readonly Levers Squeeze = new() { Batch = 0.4, Closer = 0.3, Fee = 1, Cap = true };
    private static readonly CallerContext MidwestOps =
        new("persona:mia", "Mia", "Midwest Regional Delivery Ops (illustrative)", DemoRole.RegionalOps, ["Midwest"]);

    private readonly Log _log = new();
    private readonly InMemoryPackageStore _packages = new();
    private readonly Confirmer _confirmer = new();

    private CostToServeTools ToolsFor(CallerContext caller) => new(Seed, Engine, caller, _log, _confirmer, packages: _packages);

    [Fact]
    public async Task Luis_packages_settings_as_one_proposal_with_a_friendly_unique_id()
    {
        var first = Json(await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Southeast fuel squeeze", "offset the shock", Squeeze, default));
        var second = Json(await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Lighter version", null, new Levers { Batch = 0.3 }, default));

        Assert.Equal("recorded", first.GetProperty("status").GetString());
        Assert.Equal("P-SE-01", first.GetProperty("actionId").GetString());
        Assert.Equal("P-SE-02", second.GetProperty("actionId").GetString());
        Assert.Equal("Batch Express 40% · Fill from a closer store 30% · Express fee +$1.00 · Fuel surcharge cap", first.GetProperty("levers").GetString());
        Assert.Equal(2, _packages.ReadAll().Count);
        Assert.Contains(_log.Entries, e => e.ActionId == "P-SE-01" && e.Decision == DecisionKind.Propose && e.Region == "Southeast" && e.Actor == "Luis");
    }

    [Fact]
    public async Task Playbook_lists_only_what_is_waiting_for_the_caller()
    {
        await ToolsFor(DemoPersonas.Luis).LogDecisionAsync("A2", "propose", null, null, null, default);
        await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Southeast fuel squeeze", null, Squeeze, default);
        await ToolsFor(DemoPersonas.Luis).LogDecisionAsync("A6", "propose", null, null, null, default);
        await ToolsFor(DemoPersonas.Dana).LogDecisionAsync("A6", "approve", null, null, "Southeast", default);

        var forDana = Json(ToolsFor(DemoPersonas.Dana).GetActionPlaybook(null)).GetProperty("waitingForYou");
        var forLuis = Json(ToolsFor(DemoPersonas.Luis).GetActionPlaybook(null)).GetProperty("waitingForYou");

        // Finance decides proposals only; not-reviewed actions wait for regional ops, and approvals wait to be executed.
        Assert.Equal(["A2", "P-SE-01"], forDana.EnumerateArray().Select(w => w.GetProperty("actionId").GetString()));
        Assert.All(forDana.EnumerateArray(), w => Assert.Equal("Southeast", w.GetProperty("region").GetString()));
        Assert.Equal(["A6"], forLuis.EnumerateArray().Select(w => w.GetProperty("actionId").GetString()));
    }

    [Fact]
    public async Task Nothing_is_stored_when_the_person_declines()
    {
        _confirmer.Answer = false;

        var result = Json(await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Squeeze", null, Squeeze, default));

        Assert.Equal("not_recorded", result.GetProperty("status").GetString());
        Assert.Empty(_packages.ReadAll());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Only_regional_ops_package_and_inputs_are_checked()
    {
        Assert.Equal("scope_denied", Error(await ToolsFor(DemoPersonas.Dana).ProposePackageAsync("Squeeze", null, Squeeze, default)));
        Assert.Equal("invalid_request", Error(await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("  ", null, Squeeze, default)));
        Assert.Equal("invalid_request", Error(await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Nothing", null, Levers.None, default)));
        Assert.Equal("invalid_request", Error(await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Too much", null, new Levers { Batch = 0.9 }, default)));
        Assert.Empty(_packages.ReadAll());
    }

    [Fact]
    public async Task Dana_approves_the_whole_package_for_its_region_only_and_Luis_executes_it()
    {
        await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Southeast fuel squeeze", null, Squeeze, default);
        var dana = ToolsFor(DemoPersonas.Dana);

        var wrongRegion = Json(await dana.LogDecisionAsync("P-SE-01", "approve", null, null, "West", default));
        var withSetting = Json(await dana.LogDecisionAsync("P-SE-01", "approve", null, 0.5, null, default));
        var approved = Json(await dana.LogDecisionAsync("P-SE-01", "approve", null, null, null, default));
        var executed = Json(await ToolsFor(DemoPersonas.Luis).LogDecisionAsync("P-SE-01", "execute", null, default));

        Assert.Equal("invalid_request", wrongRegion.GetProperty("error").GetString());
        Assert.Equal("invalid_request", withSetting.GetProperty("error").GetString());
        Assert.Equal("Southeast", approved.GetProperty("region").GetString());
        Assert.Equal("recorded", executed.GetProperty("status").GetString());
        Assert.Equal(["propose", "approve", "execute"], _log.Entries.Select(e => e.Decision.Verb()));
    }

    [Fact]
    public async Task An_approved_package_is_part_of_its_regions_plan_and_pilot_results()
    {
        await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Southeast fuel squeeze", null, Squeeze, default);
        var dana = ToolsFor(DemoPersonas.Dana);
        var before = Json(dana.RunScenario(0.5, 13, "Southeast", null, applyDecidedPlan: true, Levers.None));

        await dana.LogDecisionAsync("P-SE-01", "approve", null, default);
        var southeast = Json(dana.RunScenario(0.5, 13, "Southeast", null, applyDecidedPlan: true, Levers.None));
        var midwest = Json(dana.RunScenario(0.5, 13, "Midwest", null, applyDecidedPlan: true, Levers.None));
        await ToolsFor(DemoPersonas.Luis).LogDecisionAsync("P-SE-01", "execute", null, default);
        var results = Json(ToolsFor(DemoPersonas.Luis).GetExecutionResults());

        Assert.True(southeast.GetProperty("offsetOfFuelShockPct").GetDouble() > before.GetProperty("offsetOfFuelShockPct").GetDouble());
        Assert.Equal(0.4, southeast.GetProperty("levers").GetProperty("batchExpressShare").GetDouble());
        Assert.Equal(1, southeast.GetProperty("levers").GetProperty("expressFeeChangeUsd").GetDouble());
        Assert.Equal(0, midwest.GetProperty("levers").GetProperty("batchExpressShare").GetDouble());
        Assert.Equal("P-SE-01", results.GetProperty("executedActions")[0].GetProperty("actionId").GetString());
    }

    [Fact]
    public async Task Packages_are_visible_only_within_scope_and_listed_in_the_playbook()
    {
        await ToolsFor(MidwestOps).ProposePackageAsync("Midwest saver", null, new Levers { Pickup = 0.05 }, default);
        await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Southeast fuel squeeze", null, Squeeze, default);

        var luis = Json(ToolsFor(DemoPersonas.Luis).GetActionPlaybook(null));
        var danaMidwest = Json(ToolsFor(DemoPersonas.Dana).GetActionPlaybook("Midwest"));
        var luisOnMidwest = Json(await ToolsFor(DemoPersonas.Luis).LogDecisionAsync("P-MW-01", "execute", null, default));

        var luisPackages = luis.GetProperty("actions").EnumerateArray().Where(a => a.GetProperty("isPackage").GetBoolean()).ToArray();
        var row = Assert.Single(luisPackages);
        Assert.Equal("P-SE-01", row.GetProperty("actionId").GetString());
        Assert.Equal("Southeast fuel squeeze", row.GetProperty("action").GetString());
        Assert.Equal("Proposed", row.GetProperty("status").GetString());
        Assert.False(row.TryGetProperty("setting", out var setting) && setting.ValueKind != JsonValueKind.Null);
        Assert.Equal("Luis", row.GetProperty("createdBy").GetString());
        Assert.Equal(["P-MW-01"], danaMidwest.GetProperty("actions").EnumerateArray()
            .Where(a => a.GetProperty("isPackage").GetBoolean()).Select(a => a.GetProperty("actionId").GetString()));
        Assert.Equal("invalid_request", luisOnMidwest.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_package_that_breaks_the_guardrail_can_be_proposed_but_not_approved()
    {
        await ToolsFor(DemoPersonas.Luis).ProposePackageAsync("Too aggressive", null, new Levers { Batch = 0.6 }, default);

        var approve = Json(await ToolsFor(DemoPersonas.Dana).LogDecisionAsync("P-SE-01", "approve", null, default));

        Assert.Equal("scope_denied", approve.GetProperty("error").GetString());
        Assert.Contains("guardrail", approve.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_model_facing_tool_creates_a_package_from_lever_arguments()
    {
        var tool = ToolsFor(DemoPersonas.Luis).Create().Cast<AIFunction>().Single(t => t.Name == "propose_package");

        var result = Json((await tool.InvokeAsync(new AIFunctionArguments
        {
            ["name"] = "Chat package",
            ["batch_express_share"] = 0.45,
            ["carrier_surcharge_cap"] = true,
        }))!);

        Assert.Equal("P-SE-01", result.GetProperty("actionId").GetString());
        Assert.Equal(new Levers { Batch = 0.45, Cap = true }, _packages.ReadAll()[0].Levers);
    }

    [Fact]
    public void Ids_are_per_region_and_sequential()
    {
        var existing = new[] { new ActionPackage("P-SE-01", "a", "Southeast", Squeeze, "Luis", DateTimeOffset.UnixEpoch), new ActionPackage("P-SE-07", "b", "Southeast", Squeeze, "Luis", DateTimeOffset.UnixEpoch) };

        Assert.Equal("P-SE-08", PackageIds.Next("Southeast", existing));
        Assert.Equal("P-MW-01", PackageIds.Next("Midwest", existing));
        Assert.True(PackageIds.IsPackageId("p-se-08"));
        Assert.False(PackageIds.IsPackageId("A1"));
    }

    [Fact]
    public void The_jsonl_store_round_trips_and_rejects_duplicates()
    {
        var dir = Directory.CreateTempSubdirectory("cts-packages-").FullName;
        try
        {
            var store = new JsonlPackageStore(dir);
            var package = new ActionPackage("P-SE-01", "Southeast, fuel \"squeeze\"", "Southeast", Squeeze, "Luis", new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

            store.Add(package);

            Assert.Equal(package, Assert.Single(new JsonlPackageStore(dir).ReadAll()));
            Assert.Throws<InvalidOperationException>(() => store.Add(package));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions);

    private static string? Error(object value) => Json(value).GetProperty("error").GetString();

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

    private sealed class Log : IDecisionLog
    {
        public List<DecisionEntry> Entries { get; } = [];

        public IReadOnlyList<DecisionEntry> ReadAll() => Entries;

        public void Append(DecisionEntry entry) => Entries.Add(entry);
    }

    private sealed class Confirmer : IDecisionConfirmer
    {
        public bool Answer { get; set; } = true;

        public Task<bool> ConfirmAsync(DecisionProposal proposal, CancellationToken cancellationToken) => Task.FromResult(Answer);
    }
}
