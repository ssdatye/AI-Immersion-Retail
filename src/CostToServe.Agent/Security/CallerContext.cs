namespace CostToServe.Agent.Security;

public enum DemoRole
{
    /// <summary>Network-wide view; can approve or reject actions.</summary>
    FinanceAnalyst,

    /// <summary>Own region only; proposes and executes approved actions.</summary>
    RegionalOps,
}

/// <summary>
/// Who is asking, supplied by the host (never by the model or tool arguments).
/// Locally this comes from a dev persona; with Entra sign-in it comes from token claims.
/// <see cref="Id"/> is the stable owner key (Entra object ID, or the persona name).
/// </summary>
public sealed record CallerContext(string Id, string DisplayName, string Title, DemoRole Role, IReadOnlyList<string> AllowedRegions)
{
    public bool IsNetworkWide => AllowedRegions.Count == 0;

    public bool CanApprove => Role == DemoRole.FinanceAnalyst;

    public string ScopeDescription => IsNetworkWide ? "all regions" : string.Join(", ", AllowedRegions);

    public bool CanSee(string region) =>
        IsNetworkWide || AllowedRegions.Contains(region, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Illustrative demo personas for local development before Entra sign-in exists.</summary>
public static class DemoPersonas
{
    public static CallerContext Dana { get; } =
        new("persona:dana", "Dana", "Director, Fulfillment Finance (illustrative)", DemoRole.FinanceAnalyst, []);

    public static CallerContext Luis { get; } =
        new("persona:luis", "Luis", "Southeast Regional Delivery Ops Manager (illustrative)", DemoRole.RegionalOps, ["Southeast"]);

    public static CallerContext Parse(string name) => name.Trim().ToLowerInvariant() switch
    {
        "dana" => Dana,
        "luis" => Luis,
        _ => throw new ArgumentException($"Unknown persona '{name}'. Use 'dana' or 'luis'.", nameof(name)),
    };
}

/// <summary>Raised when a request falls outside the caller's role or data scope.</summary>
public sealed class ScopeDeniedException(string message) : Exception(message);
