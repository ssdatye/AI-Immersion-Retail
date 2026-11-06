using CostToServe.Core.Data;
using CostToServe.Core.State;

namespace CostToServe.Api;

/// <summary>
/// <c>reset [--target local|azure] [--yes]</c>: restores the demo to its seed baseline.
/// Asks for confirmation unless <c>--yes</c> is passed.
/// </summary>
internal static class ResetCommand
{
    private const string Usage = "Usage: reset [--target local|azure] [--yes]";

    /// <param name="azureReset">
    /// Clears the Azure demo state (Cosmos decisions, transcripts and custom packages) and describes the target, or null when
    /// Azure is not configured. Seed facts in Azure SQL are read-only and need no reset.
    /// </param>
    public static int Run(
        string[] args, ResolvedPaths paths, TextReader input, TextWriter output, TextWriter error, bool inputRedirected,
        (string Target, Func<(int Decisions, int Threads, int Packages)> Clear)? azureReset = null)
    {
        var target = "local";
        var confirmed = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Length:
                    target = args[++i].ToLowerInvariant();
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

        if (target == "azure")
        {
            if (azureReset is not { } azure)
            {
                error.WriteLine("Azure is not configured. Set CostToServe:Cosmos:Endpoint (./scripts/reset-demo.sh --target azure does this).");
                return 2;
            }

            output.WriteLine($"Reset Azure demo state in: {azure.Target}");
            output.WriteLine("Deletes every decision, chat transcript and custom package. Seed facts in Azure SQL are not changed.");
            if (!Confirm(confirmed, inputRedirected, input, output, error))
            {
                return 1;
            }

            try
            {
                var (decisions, threads, packages) = azure.Clear();
                output.WriteLine($"Done. Deleted {decisions} decisions, {threads} transcripts and {packages} packages. Refresh the studio to see the change.");
                return 0;
            }
            catch (Exception ex)
            {
                error.WriteLine($"Reset failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                return 1;
            }
        }

        if (target != "local")
        {
            error.WriteLine($"Unknown target '{target}'. {Usage}");
            return 2;
        }

        try
        {
            var state = new LocalDemoState(paths.SeedDirectory, paths.LocalStateDirectory);
            var manifest = SeedManifest.Verify(paths.SeedDirectory);
            output.WriteLine($"Reset local demo state in: {state.StateDirectory}");
            output.WriteLine($"Baseline seed: {state.SeedDirectory} (version {manifest.Version})");
            output.WriteLine($"Files to restore: {string.Join(", ", LocalDemoState.ManagedFiles)}. Other files are left untouched.");

            if (!Confirm(confirmed, inputRedirected, input, output, error))
            {
                return 1;
            }

            var result = state.Reset();
            output.WriteLine($"Done. Demo state reset to seed version {result.SeedVersion}.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            error.WriteLine($"Reset failed: {ex.Message}");
            return 1;
        }
    }

    private static bool Confirm(bool confirmed, bool inputRedirected, TextReader input, TextWriter output, TextWriter error)
    {
        if (confirmed)
        {
            return true;
        }

        if (inputRedirected)
        {
            error.WriteLine("Refusing to reset without confirmation. Re-run with --yes.");
            return false;
        }

        output.Write("Type 'reset' to continue: ");
        if (string.Equals(input.ReadLine()?.Trim(), "reset", StringComparison.Ordinal))
        {
            return true;
        }

        output.WriteLine("Cancelled. Nothing changed.");
        return false;
    }
}
