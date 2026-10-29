using System.Security.Cryptography;
using System.Text.Json;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;

namespace CostToServe.Core.Tests;

internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SeedDirectory => Path.Combine(RepoRoot, "data", "seed");

    public static string ReferenceHtml => Path.Combine(RepoRoot, "docs", "reference", "walmart-v3-cost-to-serve-demo.html");

    public static string GoldenFixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "html-engine-golden.json");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CostToServe.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (CostToServe.slnx) not found.");
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

internal static class Approx
{
    /// <summary>Relative tolerance for parity with the JavaScript reference engine.</summary>
    public const double ParityTolerance = 1e-12;

    public static void Equal(double expected, double actual, string context)
    {
        var scale = Math.Max(1, Math.Abs(expected));
        Assert.True(Math.Abs(expected - actual) <= ParityTolerance * scale,
            $"{context}: expected {expected:R}, actual {actual:R}");
    }
}

/// <summary>Golden outputs generated from the reference HTML by tools/extract-html-fixture.mjs.</summary>
internal sealed class GoldenFixture
{
    private GoldenFixture(JsonElement root)
    {
        Root = root;
        SourceSha256 = root.GetProperty("source").GetProperty("sha256").GetString()!;

        var regions = new List<string>();
        var inputs = new Dictionary<(int Week, string Region, DeliveryType Type), TypeInputs>();
        foreach (var week in root.GetProperty("baseWeeks").EnumerateObject())
        {
            foreach (var region in week.Value.EnumerateObject())
            {
                if (!regions.Contains(region.Name))
                {
                    regions.Add(region.Name);
                }

                foreach (var type in region.Value.EnumerateObject())
                {
                    var v = type.Value;
                    inputs[(int.Parse(week.Name), region.Name, DeliveryTypes.Parse(type.Name))] = new TypeInputs(
                        v.GetProperty("orders").GetDouble(),
                        v.GetProperty("split").GetDouble(),
                        v.GetProperty("die").GetDouble(),
                        v.TryGetProperty("opr", out var opr) ? opr.GetDouble() : null,
                        v.TryGetProperty("mpo", out var mpo) ? mpo.GetDouble() : null);
                }
            }
        }

        Inputs = new WeeklyInputSet(regions, inputs);
    }

    public JsonElement Root { get; }

    public string SourceSha256 { get; }

    /// <summary>The reference HTML's embedded weekly inputs (weeks 5 and 6).</summary>
    public WeeklyInputSet Inputs { get; }

    public IEnumerable<JsonElement> Cases(string name) => Root.GetProperty(name).EnumerateArray();

    public static GoldenFixture Load() => new(JsonDocument.Parse(File.ReadAllBytes(TestPaths.GoldenFixture)).RootElement.Clone());

    public static Levers ReadLevers(JsonElement e) => new()
    {
        Batch = e.GetProperty("batch").GetDouble(),
        Closer = e.GetProperty("closer").GetDouble(),
        Split = e.GetProperty("split").GetDouble(),
        Pickup = e.GetProperty("pickup").GetDouble(),
        Fee = e.GetProperty("fee").GetDouble(),
        Basket = e.GetProperty("basket").GetBoolean(),
        Cap = e.GetProperty("cap").GetBoolean(),
    };

    public static string[]? ReadRegions(JsonElement e) =>
        e.ValueKind == JsonValueKind.Null ? null : e.EnumerateArray().Select(r => r.GetString()!).ToArray();
}
