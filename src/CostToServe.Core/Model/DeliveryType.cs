namespace CostToServe.Core.Model;

public enum DeliveryType
{
    Pickup,
    SameDay,
    Express,
    Shipped,
}

public static class DeliveryTypes
{
    /// <summary>Evaluation order used by the reference engine; keep stable for numeric parity.</summary>
    public static IReadOnlyList<DeliveryType> All { get; } =
        [DeliveryType.Pickup, DeliveryType.SameDay, DeliveryType.Express, DeliveryType.Shipped];

    public static string ToSeedName(this DeliveryType type) => type switch
    {
        DeliveryType.Pickup => "Pickup",
        DeliveryType.SameDay => "Same-day",
        DeliveryType.Express => "Express",
        DeliveryType.Shipped => "Shipped",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Route-based delivery types carry orders-per-route and miles-per-order inputs.</summary>
    public static bool IsRouted(this DeliveryType type) => type is DeliveryType.SameDay or DeliveryType.Express;

    public static DeliveryType Parse(string value)
    {
        if (TryParse(value, out var type))
        {
            return type;
        }

        throw new ArgumentException(
            $"Unknown delivery type '{value}'. Expected one of: Pickup, Same-day, Express, Shipped.", nameof(value));
    }

    public static bool TryParse(string? value, out DeliveryType type)
    {
        var normalized = (value ?? string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        return Enum.TryParse(normalized, ignoreCase: true, out type) && Enum.IsDefined(type)
            && !int.TryParse(normalized, out _);
    }
}
