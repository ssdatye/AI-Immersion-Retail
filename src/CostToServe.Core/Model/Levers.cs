namespace CostToServe.Core.Model;

/// <summary>
/// What-If levers. Ranges mirror the reference What-If Studio sliders
/// (docs/reference/walmart-v3-cost-to-serve-demo.html).
/// </summary>
public sealed record Levers
{
    /// <summary>Share of Express orders batched into 2-hour windows (0-0.6).</summary>
    public double Batch { get; init; }

    /// <summary>Share of delivery orders filled from a closer store (0-0.4).</summary>
    public double Closer { get; init; }

    /// <summary>Fractional reduction in split shipments (0-0.6).</summary>
    public double Split { get; init; }

    /// <summary>Share of Same-day and Express orders nudged to Pickup (0-0.1).</summary>
    public double Pickup { get; init; }

    /// <summary>Express fee change in USD per order (-1 to 3).</summary>
    public double Fee { get; init; }

    /// <summary>Minimum basket for Express in rural zips.</summary>
    public bool Basket { get; init; }

    /// <summary>Cap carrier fuel surcharge at the contract index.</summary>
    public bool Cap { get; init; }

    public static Levers None { get; } = new();

    public UnitModifiers ToModifiers() => new(
        MilesPerOrderMultiplier: 1 - 0.30 * Closer,
        ExpressOrdersPerRouteMultiplier: 1 + 0.35 * Batch,
        SplitRateMultiplier: 1 - Split,
        SurchargeCap: Cap);

    public void Validate()
    {
        LeverLimits.EnsureInRange(nameof(Batch), Batch, LeverLimits.BatchMax);
        LeverLimits.EnsureInRange(nameof(Closer), Closer, LeverLimits.CloserMax);
        LeverLimits.EnsureInRange(nameof(Split), Split, LeverLimits.SplitMax);
        LeverLimits.EnsureInRange(nameof(Pickup), Pickup, LeverLimits.PickupMax);
        LeverLimits.EnsureInRange(nameof(Fee), Fee, LeverLimits.FeeMax, LeverLimits.FeeMin);
    }
}

public static class LeverLimits
{
    public const double BatchMax = 0.6;
    public const double CloserMax = 0.4;
    public const double SplitMax = 0.6;
    public const double PickupMax = 0.1;
    public const double FeeMin = -1;
    public const double FeeMax = 3;
    public const double DieselShiftMin = -0.5;
    public const double DieselShiftMax = 1;
    public const int DurationMinWeeks = 1;
    public const int DurationMaxWeeks = 13;

    public static void EnsureInRange(string name, double value, double max, double min = 0)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {min} and {max}.");
        }
    }
}
