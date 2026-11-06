using System.Diagnostics;
using CostToServe.Api.Cloud;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace CostToServe.Api.Studio;

/// <summary>
/// Opens the data and sign-in connections before the app takes traffic, so the first click after a restart
/// (for example a demo reset) is as fast as the rest. Without it, the first request opened every Cosmos DB
/// container and fetched Entra's signing keys, which took about 3 seconds.
/// </summary>
public static class Warmup
{
    public static async Task RunAsync(IServiceProvider services, bool entraConfigured, ILogger logger, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (services.GetService<CosmosStores>() is { } cosmos)
            {
                foreach (var container in new[] { cosmos.Decisions, cosmos.Threads, cosmos.Scenarios })
                {
                    using var feed = container.GetItemQueryIterator<object>(new QueryDefinition("SELECT TOP 1 c.id FROM c"));
                    await feed.ReadNextAsync(ct).ConfigureAwait(false);
                }
            }

            var studio = services.GetRequiredService<StudioServices>();
            _ = studio.DecisionLog.ReadAll();
            _ = studio.Packages?.ReadAll();

            if (entraConfigured
                && services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).ConfigurationManager is { } keys)
            {
                await keys.GetConfigurationAsync(ct).ConfigureAwait(false);
            }

            logger.LogInformation("Warm-up finished in {ElapsedMs} ms.", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The app still starts; the first request is slower.
            logger.LogWarning(ex, "Warm-up failed after {ElapsedMs} ms; connections open on the first request instead.", watch.ElapsedMilliseconds);
        }
    }
}
