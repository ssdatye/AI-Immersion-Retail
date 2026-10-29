using CostToServe.Core.Data;
using CostToServe.Core.Model;
using CostToServe.Core.State;

namespace CostToServe.Core.Tests;

public sealed class SeedDataAndResetTests : IDisposable
{
    private static readonly SeedDataSet Seed = SeedDataSet.Load(TestPaths.SeedDirectory);
    private readonly string _temp = Directory.CreateTempSubdirectory("cts-tests-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Committed_seed_matches_its_manifest()
    {
        var result = SeedManifest.Verify(TestPaths.SeedDirectory);

        Assert.True(result.IsValid, string.Join(" ", result.Problems));
        Assert.Equal(12, result.Version.Length);
    }

    [Fact]
    public void Text_copies_of_the_seed_tables_load_identically()
    {
        // Azure SQL stores each CSV field as text and returns the same tables; simulate that copy.
        var copied = SeedDataSet.ReadTables(TestPaths.SeedDirectory).ToDictionary(
            t => t.Key,
            t => new SeedTable(t.Key, [.. t.Value.Columns], t.Value.Rows.Select(r => r.Select(f => new string(f.AsSpan())).ToArray()).ToArray()));

        var fromCopy = SeedDataSet.Load(copied, "copy");
        var levers = new Levers { Batch = 0.35, Closer = 0.2 };

        Assert.Equal(SeedDataSet.TableFiles.Count, copied.Count);
        Assert.Equal(Seed.Costs, fromCopy.Costs);
        Assert.Equal(
            new Engine.CostEngine(Seed.WeeklyInputs).Scenario(levers, 0.5, 13).NetOperatingIncomeImpact,
            new Engine.CostEngine(fromCopy.WeeklyInputs).Scenario(levers, 0.5, 13).NetOperatingIncomeImpact);
        Assert.Throws<InvalidDataException>(() => SeedDataSet.Load(copied.Where(t => t.Key != "zones.csv").ToDictionary(), "partial"));
    }

    [Fact]
    public void Manifest_detects_changed_and_unlisted_files()
    {
        var seed = CopySeed();
        File.AppendAllText(Path.Combine(seed, "zones.csv"), "Southeast,Rogue Zone,Urban,1.0\n");
        File.WriteAllText(Path.Combine(seed, "extra.csv"), "a,b\n");

        var result = SeedManifest.Verify(seed);

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("zones.csv has changed"));
        Assert.Contains(result.Problems, p => p.Contains("extra.csv is not listed"));
    }

    [Fact]
    public void Cost_query_defaults_to_the_latest_complete_week_network_wide()
    {
        var result = Seed.QueryCostPerOrder(new CostQuery());

        Assert.Equal("Network", result.Scope);
        Assert.Equal(new DateOnly(2026, 10, 19), result.From);
        Assert.Equal(7, result.Days);
        Assert.Equal(result.TotalCost / result.Orders, result.CostPerOrder);
    }

    [Fact]
    public void Cost_query_by_zone_infers_its_region()
    {
        var zone = Seed.QueryCostPerOrder(new CostQuery(Zone: "southeast zone 1", DeliveryType: DeliveryType.Express));
        var region = Seed.QueryCostPerOrder(new CostQuery("Southeast", DeliveryType: DeliveryType.Express));

        Assert.Equal("Southeast", zone.Region);
        Assert.Equal("Southeast Zone 1", zone.Zone);
        Assert.InRange(zone.Orders, 1, region.Orders - 1);
    }

    [Fact]
    public void Cost_query_rejects_invalid_scope_and_dates()
    {
        Assert.Throws<ArgumentException>(() => Seed.QueryCostPerOrder(new CostQuery("Midwest", "Southeast Zone 1")));
        Assert.Throws<ArgumentException>(() => Seed.QueryCostPerOrder(new CostQuery(Zone: "Nowhere")));
        Assert.Throws<ArgumentException>(() => Seed.QueryCostPerOrder(new CostQuery("Northeast")));
        Assert.Throws<ArgumentException>(() => Seed.QueryCostPerOrder(new CostQuery(From: new DateOnly(2026, 10, 20), To: new DateOnly(2026, 10, 19))));
        Assert.Throws<ArgumentException>(() => Seed.QueryCostPerOrder(new CostQuery(From: new DateOnly(2026, 9, 1))));
    }

    [Fact]
    public void Reset_restores_the_decision_log_and_keeps_unmanaged_files()
    {
        var seed = CopySeed();
        var stateDir = Path.Combine(_temp, "local");
        var state = new LocalDemoState(seed, stateDir);
        state.EnsureInitialized();
        var log = Path.Combine(stateDir, LocalDemoState.DecisionLogFile);
        File.AppendAllText(log, "2026-10-26T09:00:00Z,A1,Luis,accept,,\n");
        File.WriteAllText(Path.Combine(stateDir, "notes.txt"), "keep me");

        var result = state.Reset();

        Assert.Equal("timestamp,action_id,approver,decision,reason,next_day_result\n", File.ReadAllText(log));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(stateDir, "notes.txt")));
        Assert.Equal([LocalDemoState.DecisionLogFile, LocalDemoState.ChatTranscriptsFile], result.FilesReset);
    }

    [Fact]
    public void Initialization_never_overwrites_existing_state()
    {
        var stateDir = Path.Combine(_temp, "local");
        var state = new LocalDemoState(CopySeed(), stateDir);
        state.EnsureInitialized();
        var log = Path.Combine(stateDir, LocalDemoState.DecisionLogFile);
        File.AppendAllText(log, "row\n");

        state.EnsureInitialized();

        Assert.EndsWith("row\n", File.ReadAllText(log));
    }

    [Fact]
    public void Reset_refuses_a_tampered_seed()
    {
        var seed = CopySeed();
        File.AppendAllText(Path.Combine(seed, "orders_daily.csv"), "\n");
        var state = new LocalDemoState(seed, Path.Combine(_temp, "local"));

        var error = Assert.Throws<InvalidOperationException>(state.Reset);
        Assert.Contains("orders_daily.csv has changed", error.Message);
    }

    [Fact]
    public void Reset_refuses_a_non_empty_directory_it_does_not_own()
    {
        var stateDir = Path.Combine(_temp, "someone-elses");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "important.txt"), "do not touch");
        var state = new LocalDemoState(CopySeed(), stateDir);

        Assert.Throws<InvalidOperationException>(state.Reset);
        Assert.True(File.Exists(Path.Combine(stateDir, "important.txt")));
        Assert.False(File.Exists(Path.Combine(stateDir, LocalDemoState.DecisionLogFile)));
    }

    [Fact]
    public void State_directory_must_be_separate_from_the_seed()
    {
        var seed = CopySeed();

        Assert.Throws<ArgumentException>(() => new LocalDemoState(seed, seed));
        Assert.Throws<ArgumentException>(() => new LocalDemoState(seed, Path.Combine(seed, "local")));
        Assert.Throws<ArgumentException>(() => new LocalDemoState(seed, _temp));
    }

    private string CopySeed()
    {
        var target = Path.Combine(_temp, "seed");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(TestPaths.SeedDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }
}
