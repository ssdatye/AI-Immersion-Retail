namespace CostToServe.Api;

public sealed class CostToServeOptions
{
    public const string SectionName = "CostToServe";

    public const string CsvMode = "Csv";
    public const string AzureMode = "Azure";

    /// <summary>
    /// Data source. "Csv" (local dev): seed CSVs and a local decision log. "Azure": cost facts from
    /// Azure SQL, decisions and chat transcripts in Cosmos DB, all through managed identity.
    /// </summary>
    public string DataMode { get; init; } = CsvMode;

    public bool IsAzure => string.Equals(DataMode, AzureMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Azure SQL database with the seed tables. No credentials: tokens come from the app's identity.</summary>
    public SqlOptions Sql { get; init; } = new();

    public CosmosOptions Cosmos { get; init; } = new();

    /// <summary>Read-only seed CSVs. Relative paths resolve from the repository root.</summary>
    public string SeedDirectory { get; init; } = "data/seed";

    /// <summary>Mutable local demo state (git-ignored). Relative paths resolve from the repository root.</summary>
    public string LocalStateDirectory { get; init; } = "data/local";

    /// <summary>Lever settings that model each playbook action. Relative paths resolve from the repository root.</summary>
    public string ActionConfigFile { get; init; } = "data/config/action-levers.csv";

    /// <summary>
    /// Accepts the X-Demo-Persona header (dana or luis) as the caller. Local development only,
    /// enabled in appsettings.Development.json; Entra sign-in replaces it.
    /// </summary>
    public bool DevPersonas { get; init; }

    public ResolvedPaths Resolve(string contentRoot)
    {
        if (!IsAzure && !string.Equals(DataMode, CsvMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"DataMode '{DataMode}' is not supported. Use '{CsvMode}' or '{AzureMode}'.");
        }

        var repoRoot = FindRepositoryRoot(contentRoot) ?? FindRepositoryRoot(AppContext.BaseDirectory);
        string Full(string path)
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            // Deployed apps have no repository; Azure mode resolves shipped files from the content root.
            if (repoRoot is null && IsAzure)
            {
                return Path.GetFullPath(Path.Combine(contentRoot, path));
            }

            return repoRoot is not null
                ? Path.GetFullPath(Path.Combine(repoRoot, path))
                : throw new InvalidOperationException(
                    $"Cannot resolve relative path '{path}': repository root (CostToServe.slnx) not found. " +
                    "Set CostToServe__SeedDirectory and CostToServe__LocalStateDirectory to absolute paths.");
        }

        var localSettings = repoRoot is null ? null : Path.Combine(repoRoot, "src", "CostToServe.Api", "appsettings.Local.json");
        return new ResolvedPaths(Full(SeedDirectory), Full(LocalStateDirectory), localSettings, Full(ActionConfigFile));
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CostToServe.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}

public sealed class SqlOptions
{
    /// <summary>For example "Server=tcp:x.database.windows.net,1433;Database=y;Encrypt=True". No user or password.</summary>
    public string? ConnectionString { get; init; }

    public string TokenScope { get; init; } = "https://database.windows.net/.default";
}

public sealed class CosmosOptions
{
    public string? Endpoint { get; init; }

    public string Database { get; init; } = "costtoserve";
}

public sealed record ResolvedPaths(string SeedDirectory, string LocalStateDirectory, string? LocalSettingsFile = null, string? ActionConfigFile = null);
