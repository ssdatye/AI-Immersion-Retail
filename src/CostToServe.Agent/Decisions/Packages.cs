using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;

namespace CostToServe.Agent.Decisions;

/// <summary>
/// A custom action: a combination of lever settings that regional ops packaged from the model and proposed
/// as one decision, for example P-SE-01 "Southeast fuel squeeze". It applies to its region only and is
/// decided as defined (no adjustable setting); it follows the same workflow as playbook actions A1 to A6.
/// </summary>
public sealed record ActionPackage(string Id, string Name, string Region, Levers Levers, string CreatedBy, DateTimeOffset CreatedAt)
{
    public const int MaxNameLength = 60;

    public DemoAction ToAction() => new(Id, Levers, $"custom package by {CreatedBy}", Adjustable: false);

    /// <summary>The package's levers in words, for example "Batch Express 45% · Express fee +$1.00 · surcharge cap".</summary>
    public string Summary => Describe(Levers);

    public static string Describe(Levers l)
    {
        static string P(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        var parts = new List<string>();
        if (l.Batch > 0) parts.Add($"Batch Express {P(l.Batch)}");
        if (l.Closer > 0) parts.Add($"Fill from a closer store {P(l.Closer)}");
        if (l.Split > 0) parts.Add($"Reduce split shipments {P(l.Split)}");
        if (l.Pickup > 0) parts.Add($"Nudge to Pickup {P(l.Pickup)}");
        if (l.Fee != 0) parts.Add($"Express fee {(l.Fee > 0 ? "+" : "-")}${Math.Abs(l.Fee).ToString("0.00", CultureInfo.InvariantCulture)}");
        if (l.Basket) parts.Add("Rural minimum basket");
        if (l.Cap) parts.Add("Fuel surcharge cap");
        return parts.Count == 0 ? "No levers" : string.Join(" · ", parts);
    }

    /// <summary>True when at least one lever is set.</summary>
    public static bool HasAny(Levers l) => l.Batch > 0 || l.Closer > 0 || l.Split > 0 || l.Pickup > 0 || l.Fee != 0 || l.Basket || l.Cap;
}

/// <summary>Package IDs: P-{region code}-{nn}, for example P-SE-01. Unique per region by sequence.</summary>
public static partial class PackageIds
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Southeast"] = "SE",
        ["Midwest"] = "MW",
        ["West"] = "W",
    };

    public static string RegionCode(string region) =>
        Codes.TryGetValue(region, out var code) ? code : new string(region.Where(char.IsLetter).Take(2).ToArray()).ToUpperInvariant();

    /// <summary>The next free ID for the region, given the packages that exist.</summary>
    public static string Next(string region, IEnumerable<ActionPackage> existing)
    {
        var prefix = $"P-{RegionCode(region)}-";
        var used = existing.Select(p => p.Id).Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(id => int.TryParse(id[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"{prefix}{used + 1:00}";
    }

    public static bool IsPackageId(string? id) => id is not null && Pattern().IsMatch(id.Trim());

    [GeneratedRegex(@"^P-[A-Z]{1,3}-\d{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}

/// <summary>Where custom packages are kept. Packages are never edited or deleted, except by a demo reset.</summary>
public interface IPackageStore
{
    IReadOnlyList<ActionPackage> ReadAll();

    /// <summary>Adds a package. Throws <see cref="InvalidOperationException"/> when the ID is already taken.</summary>
    void Add(ActionPackage package);
}

/// <summary>Packages in memory, for tests and hosts without demo state.</summary>
public sealed class InMemoryPackageStore : IPackageStore
{
    private readonly List<ActionPackage> _items = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<ActionPackage> ReadAll()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }

    public void Add(ActionPackage package)
    {
        lock (_gate)
        {
            if (_items.Any(p => string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Package {package.Id} already exists.");
            }

            _items.Add(package);
        }
    }
}

/// <summary>CSV-mode packages: one JSON object per line in the local demo state (data/local/packages.jsonl).</summary>
public sealed class JsonlPackageStore(string stateDirectory) : IPackageStore
{
    public const string FileName = CostToServe.Core.State.LocalDemoState.PackagesFile;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    public string Path { get; } = System.IO.Path.Combine(stateDirectory, FileName);

    public IReadOnlyList<ActionPackage> ReadAll()
    {
        lock (_gate)
        {
            return ReadAllUnlocked().ToArray();
        }
    }

    public void Add(ActionPackage package)
    {
        lock (_gate)
        {
            if (ReadAllUnlocked().Any(p => string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Package {package.Id} already exists.");
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, JsonSerializer.Serialize(package, Json) + "\n");
        }
    }

    private IEnumerable<ActionPackage> ReadAllUnlocked() =>
        File.Exists(Path) ? File.ReadLines(Path).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonSerializer.Deserialize<ActionPackage>(l, Json)!) : [];
}
