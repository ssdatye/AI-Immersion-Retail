using System.Security.Claims;
using CostToServe.Agent.Security;

namespace CostToServe.Api.Studio;

/// <summary>Entra ID sign-in for the studio (single app registration: SPA + API scope + app roles).</summary>
public sealed class EntraOptions
{
    public const string SectionName = "Entra";
    public const string ScopeName = "access_as_user";
    public const string FinanceAnalystRole = "FinanceAnalyst";
    public const string RegionalOpsRole = "RegionalOps";

    public string? TenantId { get; init; }

    public string? ClientId { get; init; }

    /// <summary>
    /// Region for each RegionalOps user, keyed by Entra object ID. A RegionalOps user without
    /// an entry is denied (fail closed). Lives in git-ignored local settings or App Service settings.
    /// </summary>
    public Dictionary<string, string> RegionalOpsRegions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);

    public string Authority => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public string ApiScope => $"api://{ClientId}/{ScopeName}";
}

public enum CallerResolution
{
    Resolved,
    NotSignedIn,
    NoRole,
    NoRegion,
}

/// <summary>Maps a validated Entra token to a caller. Roles come only from the token's app roles.</summary>
public static class EntraCallerMapper
{
    public static (CallerResolution Result, CallerContext? Caller) Map(ClaimsPrincipal user, EntraOptions options, IReadOnlyList<string> knownRegions)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return (CallerResolution.NotSignedIn, null);
        }

        var objectId = user.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(objectId))
        {
            return (CallerResolution.NotSignedIn, null);
        }

        var name = user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value ?? "Signed-in user";
        var roles = user.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        // FinanceAnalyst takes precedence, so one person never holds approve and execute rights together.
        if (roles.Contains(EntraOptions.FinanceAnalystRole))
        {
            return (CallerResolution.Resolved, new CallerContext(objectId, name, "Fulfillment Finance", DemoRole.FinanceAnalyst, []));
        }

        if (roles.Contains(EntraOptions.RegionalOpsRole))
        {
            var region = options.RegionalOpsRegions.GetValueOrDefault(objectId);
            var canonical = knownRegions.FirstOrDefault(r => string.Equals(r, region, StringComparison.OrdinalIgnoreCase));
            return canonical is null
                ? (CallerResolution.NoRegion, null)
                : (CallerResolution.Resolved, new CallerContext(objectId, name, $"{canonical} Regional Delivery Ops", DemoRole.RegionalOps, [canonical]));
        }

        return (CallerResolution.NoRole, null);
    }
}
