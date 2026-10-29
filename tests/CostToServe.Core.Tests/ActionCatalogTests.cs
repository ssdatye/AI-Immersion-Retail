using CostToServe.Core.Engine;

namespace CostToServe.Core.Tests;

public sealed class ActionCatalogTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("cts-actions-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static string ConfigFile => Path.Combine(TestPaths.RepoRoot, "data", "config", "action-levers.csv");

    [Fact]
    public void Shipped_config_matches_the_built_in_defaults()
    {
        var loaded = ActionCatalog.Load(ConfigFile);

        Assert.Equal(ActionCatalog.Default.All, loaded.All);
        Assert.Equal(["A1", "A2", "A3"], loaded.All.Where(a => !a.LeversDerivedFromPlaybookText).Select(a => a.Id));
        Assert.Equal(0.03, loaded.Find("a6")!.Levers.Pickup);
    }

    [Fact]
    public void Edited_values_are_used()
    {
        var path = Write("A6,0,0,0,0.05,0,false,false,derived (edited)");

        Assert.Equal(0.05, ActionCatalog.Load(path).Find("A6")!.Levers.Pickup);
    }

    [Theory]
    [InlineData("A6,0,0,0,0.5,0,false,false,too high")]
    [InlineData("A6,0,0,0,x,0,false,false,not a number")]
    [InlineData("A6,0,0,0,0.03,0,maybe,false,bad switch")]
    public void Invalid_rows_are_rejected(string row)
    {
        Assert.Throws<InvalidDataException>(() => ActionCatalog.Load(Write(row)));
    }

    [Fact]
    public void Duplicate_actions_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => ActionCatalog.Load(Write("A6,0,0,0,0.03,0,false,false,x", "a6,0,0,0,0.04,0,false,false,y")));
    }

    [Fact]
    public void Missing_file_falls_back_to_defaults()
    {
        Assert.Same(ActionCatalog.Default, ActionCatalog.LoadOrDefault(Path.Combine(_temp, "missing.csv")));
    }

    [Fact]
    public void Share_actions_have_one_adjustable_setting_and_switch_actions_have_none()
    {
        var c = ActionCatalog.Default;

        Assert.Equal(["batch", "closer", "split", "pickup"], new[] { "A1", "A2", "A4", "A6" }.Select(id => c.Find(id)!.Setting!.Lever));
        Assert.Null(c.Find("A3")!.Setting);
        Assert.Null(c.Find("A5")!.Setting);
        Assert.Equal(0.35, c.Find("A1")!.Setting!.Default);
        Assert.Equal(0.1, c.Find("A6")!.Setting!.Max);
        Assert.Equal("Shift to Pickup 5%", c.Find("A6")!.Setting!.Format(0.05));
        Assert.Equal("Split shipments -40%", c.Find("A4")!.Setting!.Format(0.4));
    }

    [Fact]
    public void A_setting_replaces_only_the_adjustable_lever_and_is_range_checked()
    {
        var a6 = ActionCatalog.Default.Find("A6")!;

        Assert.Equal(a6.Levers with { Pickup = 0.05 }, a6.WithSetting(0.05));
        Assert.Same(a6.Levers, a6.WithSetting(null));
        Assert.Throws<ArgumentException>(() => a6.WithSetting(0.2));
        Assert.Throws<ArgumentException>(() => a6.WithSetting(0));
        Assert.Throws<ArgumentException>(() => ActionCatalog.Default.Find("A3")!.WithSetting(0.1));
    }

    private string Write(params string[] rows)
    {
        var path = Path.Combine(_temp, $"{Guid.NewGuid():n}.csv");
        File.WriteAllLines(path, ["action_id,batch,closer,split,pickup,fee,basket,cap,source", .. rows]);
        return path;
    }
}
