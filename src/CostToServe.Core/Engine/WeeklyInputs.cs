using CostToServe.Core.Model;

namespace CostToServe.Core.Engine;

/// <summary>Source of weekly region-level inputs for the cost engine.</summary>
public interface IWeeklyInputs
{
    /// <summary>Canonical region names, in reference evaluation order.</summary>
    IReadOnlyList<string> Regions { get; }

    /// <summary>Latest complete week; scenarios re-cost at this week's volumes.</summary>
    int CurrentWeek { get; }

    int PriorWeek { get; }

    TypeInputs Get(int week, string region, DeliveryType type);

    /// <summary>Returns the canonical region name, or throws <see cref="ArgumentException"/> for unknown regions.</summary>
    string CanonicalRegion(string region);
}

/// <summary>In-memory weekly inputs keyed by week, region and delivery type.</summary>
public sealed class WeeklyInputSet : IWeeklyInputs
{
    private readonly Dictionary<(int Week, string Region, DeliveryType Type), TypeInputs> _inputs;
    private readonly Dictionary<string, string> _canonicalRegions;

    public WeeklyInputSet(IEnumerable<string> regions, IReadOnlyDictionary<(int Week, string Region, DeliveryType Type), TypeInputs> inputs)
    {
        Regions = regions.ToArray();
        if (Regions.Count == 0)
        {
            throw new ArgumentException("At least one region is required.", nameof(regions));
        }

        _canonicalRegions = Regions.ToDictionary(r => r, r => r, StringComparer.OrdinalIgnoreCase);
        _inputs = inputs.ToDictionary(kv => (kv.Key.Week, CanonicalRegion(kv.Key.Region), kv.Key.Type), kv => kv.Value);

        var weeks = _inputs.Keys.Select(k => k.Week).Distinct().Order().ToArray();
        CurrentWeek = weeks.Length > 0 ? weeks[^1] : throw new ArgumentException("No weekly inputs supplied.", nameof(inputs));
        PriorWeek = weeks.Length > 1 ? weeks[^2] : throw new ArgumentException("At least two weeks are required.", nameof(inputs));

        foreach (var week in new[] { PriorWeek, CurrentWeek })
        {
            foreach (var region in Regions)
            {
                foreach (var type in DeliveryTypes.All)
                {
                    var value = Get(week, region, type);
                    if (type.IsRouted() && (value.OrdersPerRoute is not > 0 || value.MilesPerOrder is null))
                    {
                        throw new ArgumentException($"Week {week} {region} {type.ToSeedName()} is missing route inputs.", nameof(inputs));
                    }
                }
            }
        }
    }

    public IReadOnlyList<string> Regions { get; }

    public int CurrentWeek { get; }

    public int PriorWeek { get; }

    public TypeInputs Get(int week, string region, DeliveryType type) =>
        _inputs.TryGetValue((week, CanonicalRegion(region), type), out var value)
            ? value
            : throw new ArgumentException($"No inputs for week {week}, region '{region}', type {type.ToSeedName()}.");

    public string CanonicalRegion(string region) =>
        _canonicalRegions.TryGetValue(region, out var canonical)
            ? canonical
            : throw new ArgumentException($"Unknown region '{region}'. Expected one of: {string.Join(", ", Regions)}.", nameof(region));
}
