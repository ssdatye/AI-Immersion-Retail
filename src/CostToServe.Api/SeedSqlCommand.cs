using Azure.Core;
using CostToServe.Api.Cloud;
using CostToServe.Core.Data;

namespace CostToServe.Api;

/// <summary>
/// <c>seed-sql [--reader-identity name] [--yes]</c>: loads the verified seed CSVs into the demo's
/// Azure SQL database (schema <c>seed</c>, replaced each time) and grants the web app's identity
/// read access. Runs with the developer's Entra sign-in; the server must list them as Entra admin.
/// </summary>
internal static class SeedSqlCommand
{
    private const string Usage = "Usage: seed-sql [--reader-identity <managed identity name>] [--yes]";

    public static async Task<int> RunAsync(
        string[] args, CostToServeOptions options, ResolvedPaths paths, TokenCredential credential,
        TextReader input, TextWriter output, TextWriter error, bool inputRedirected)
    {
        string? reader = null;
        var confirmed = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--reader-identity" when i + 1 < args.Length:
                    reader = args[++i];
                    break;
                case "--yes" or "-y":
                    confirmed = true;
                    break;
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                default:
                    error.WriteLine($"Unknown argument '{args[i]}'. {Usage}");
                    return 2;
            }
        }

        var manifest = SeedManifest.Verify(paths.SeedDirectory);
        if (!manifest.IsValid)
        {
            error.WriteLine("Seed data does not match its manifest; refusing to load it. " + string.Join(" ", manifest.Problems));
            return 1;
        }

        if (string.IsNullOrWhiteSpace(options.Sql.ConnectionString))
        {
            error.WriteLine("Set CostToServe__Sql__ConnectionString (./scripts/seed-azure-sql.sh does this).");
            return 2;
        }

        var server = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(options.Sql.ConnectionString);
        output.WriteLine($"Load seed version {manifest.Version} into {server.DataSource} / {server.InitialCatalog}, schema '{SqlSeedStore.Schema}'.");
        output.WriteLine($"Replaces the {SeedDataSet.TableFiles.Count} seed tables; nothing outside that schema is changed.");
        if (!confirmed)
        {
            if (inputRedirected)
            {
                error.WriteLine("Refusing to load without confirmation. Re-run with --yes.");
                return 1;
            }

            output.Write("Type 'seed' to continue: ");
            if (!string.Equals(input.ReadLine()?.Trim(), "seed", StringComparison.Ordinal))
            {
                output.WriteLine("Cancelled. Nothing changed.");
                return 1;
            }
        }

        try
        {
            await SqlSeedStore.SeedAsync(options.Sql, credential, SeedDataSet.ReadTables(paths.SeedDirectory), manifest.Version, reader, output);
            var (tables, version) = await SqlSeedStore.LoadAsync(options.Sql, credential);
            var check = SeedDataSet.Load(tables, "Azure SQL");
            output.WriteLine($"Done. Read back seed version {version}: {check.Costs.Count} cost rows, {check.Zones.Count} zones.");
            return 0;
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or global::Azure.Identity.AuthenticationFailedException or InvalidDataException)
        {
            error.WriteLine($"Seeding failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }
}
