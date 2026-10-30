using System.Text.Json;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Security;
using CostToServe.Agent.Tools;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;
using Microsoft.Extensions.AI;

namespace CostToServe.Agent.Tests;

/// <summary>
/// Regional ops proposes a revised setting for an approved action. The approval stays in force until finance
/// approves the revision (the new setting applies) or rejects it (only the revision is declined).
/// </summary>
public sealed class RevisionTests
{
    private static readonly SeedDataSet Seed = SeedDataSet.Load(SeedDirectory());
    private static readonly CostEngine Engine = new(Seed.WeeklyInputs);

    private readonly Log _log = new();
    private readonly InMemoryPackageStore _packages = new();

    private CostToServeTools Luis => new(Seed, Engine, DemoPersonas.Luis, _log, new Yes(), packages: _packages);

    private CostToServeTools Dana => new(Seed, Engine, DemoPersonas.Dana, _log, new Yes(), packages: _packages);

    private async Task ApproveA2At20()
    {
        Recorded(await Luis.LogDecisionAsync("A2", "propose", null, null, null, default));
        Recorded(await Dana.LogDecisionAsync("A2", "approve", null, null, "Southeast", default));
    }

    [Fact]
    public async Task A_revision_keeps_the_approval_in_force_until_finance_decides()
    {
        await ApproveA2At20();

        var revised = Json(await Luis.LogDecisionAsync("A2", "propose", "Pilot showed room", 0.3, null, default));

        Assert.Equal("recorded", revised.GetProperty("status").GetString());
        Assert.StartsWith("proposed", revised.GetProperty("revision").GetString());
        var row = A2(Dana);
        Assert.Equal("Approved", row.GetProperty("status").GetString());
        Assert.Equal(0.2, row.GetProperty("setting").GetProperty("value").GetDouble());
        Assert.Equal(0.3, row.GetProperty("pendingRevision").GetProperty("value").GetDouble());
        Assert.Equal(["approve", "reject"], row.GetProperty("availableDecisions").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(0.2, PlanCloser(Dana));
        Assert.Contains("A2", WaitingFor(Dana));
        Assert.DoesNotContain("A2", WaitingFor(Luis));
    }

    [Fact]
    public async Task Approving_accepts_the_revised_setting()
    {
        await ApproveA2At20();
        Recorded(await Luis.LogDecisionAsync("A2", "propose", null, 0.3, null, default));

        var approved = Json(await Dana.LogDecisionAsync("A2", "approve", null, null, null, default));

        Assert.Equal("recorded", approved.GetProperty("status").GetString());
        Assert.Equal("accepted", approved.GetProperty("revision").GetString());
        Assert.Equal(0.3, approved.GetProperty("settingValue").GetDouble());
        Assert.False(A2(Dana).TryGetProperty("pendingRevision", out _));
        Assert.Equal(0.3, PlanCloser(Dana));
    }

    [Fact]
    public async Task Rejecting_declines_only_the_revision()
    {
        await ApproveA2At20();
        Recorded(await Luis.LogDecisionAsync("A2", "propose", null, 0.3, null, default));

        var declined = Json(await Dana.LogDecisionAsync("A2", "reject", "Keep the pilot at 20%", null, "Southeast", default));

        Assert.StartsWith("declined", declined.GetProperty("revision").GetString());
        var row = A2(Dana);
        Assert.Equal("Approved", row.GetProperty("status").GetString());
        Assert.Equal(0.2, row.GetProperty("setting").GetProperty("value").GetDouble());
        Assert.False(row.TryGetProperty("pendingRevision", out _));
        Assert.Equal(0.2, PlanCloser(Dana));
    }

    [Fact]
    public async Task Executing_runs_the_approved_setting_and_the_revision_lapses()
    {
        await ApproveA2At20();
        Recorded(await Luis.LogDecisionAsync("A2", "propose", null, 0.3, null, default));

        var executed = Json(await Luis.LogDecisionAsync("A2", "execute", null, null, null, default));

        Assert.StartsWith("lapsed", executed.GetProperty("revision").GetString());
        Assert.Equal(0.2, executed.GetProperty("settingValue").GetDouble());
        Assert.Equal("Executed", A2(Dana).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_revision_needs_a_new_setting_and_an_adjustable_action()
    {
        await ApproveA2At20();
        Recorded(await Luis.LogDecisionAsync("A5", "propose", null, null, null, default));
        Recorded(await Dana.LogDecisionAsync("A5", "approve", null, null, "Southeast", default));

        Assert.Equal("invalid_request", Error(await Luis.LogDecisionAsync("A2", "propose", null, null, null, default)));
        Assert.Equal("conflict", Error(await Luis.LogDecisionAsync("A2", "propose", null, 0.2, null, default)));
        Assert.Equal("invalid_request", Error(await Luis.LogDecisionAsync("A5", "propose", null, null, null, default)));
        Assert.Equal(["execute", "propose"], A2(Luis).GetProperty("availableDecisions").EnumerateArray().Select(d => d.GetString()));
    }

    private static void Recorded(object result) => Assert.Equal("recorded", Json(result).GetProperty("status").GetString());

    private static JsonElement A2(CostToServeTools tools) =>
        Json(tools.GetActionPlaybook("Southeast")).GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("actionId").GetString() == "A2");

    private static string[] WaitingFor(CostToServeTools tools) =>
        [.. Json(tools.GetActionPlaybook(null)).GetProperty("waitingForYou").EnumerateArray().Select(w => w.GetProperty("actionId").GetString()!)];

    private static double PlanCloser(CostToServeTools tools) =>
        Json(tools.RunScenario(0.5, 13, "Southeast", null, applyDecidedPlan: true, Levers.None)).GetProperty("levers").GetProperty("closerStoreShare").GetDouble();

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
        private readonly List<DecisionEntry> _entries = [];

        public IReadOnlyList<DecisionEntry> ReadAll() => _entries;

        public void Append(DecisionEntry entry) => _entries.Add(entry);
    }

    private sealed class Yes : IDecisionConfirmer
    {
        public Task<bool> ConfirmAsync(DecisionProposal proposal, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
