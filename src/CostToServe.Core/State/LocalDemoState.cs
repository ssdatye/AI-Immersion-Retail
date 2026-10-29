using CostToServe.Core.Data;

namespace CostToServe.Core.State;

public sealed record ResetResult(string StateDirectory, string SeedVersion, IReadOnlyList<string> FilesReset);

/// <summary>
/// Local (CSV-mode) mutable demo state. Reset restores the deterministic baseline:
/// it verifies the read-only seed against its manifest, then recreates only the
/// files this class manages. It never deletes other files or directories.
/// </summary>
public sealed class LocalDemoState
{
    public const string DecisionLogFile = "decision_log.csv";
    public const string ChatTranscriptsFile = "chat_transcripts.jsonl";
    public const string MarkerFile = ".cost-to-serve-demo-state";

    /// <summary>Files owned by the demo state; later increments add theirs here.</summary>
    public static IReadOnlyList<string> ManagedFiles { get; } = [DecisionLogFile, ChatTranscriptsFile];

    public LocalDemoState(string seedDirectory, string stateDirectory)
    {
        SeedDirectory = Path.GetFullPath(seedDirectory);
        StateDirectory = Path.GetFullPath(stateDirectory);

        if (IsSameOrInside(StateDirectory, SeedDirectory) || IsSameOrInside(SeedDirectory, StateDirectory))
        {
            throw new ArgumentException("The local state directory must be separate from the seed directory.", nameof(stateDirectory));
        }
    }

    public string SeedDirectory { get; }

    public string StateDirectory { get; }

    /// <summary>Creates state from the seed only if it does not exist yet. Never overwrites.</summary>
    public void EnsureInitialized()
    {
        if (File.Exists(Path.Combine(StateDirectory, MarkerFile)))
        {
            return;
        }

        EnsureOwnedOrEmpty();
        WriteBaseline();
    }

    /// <summary>Restores the managed files to the seed baseline. Destructive for local demo state only.</summary>
    public ResetResult Reset()
    {
        var manifest = SeedManifest.Verify(SeedDirectory);
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException(
                "Seed data does not match its manifest; refusing to reset. Restore it with 'git checkout -- data/seed'. Problems: "
                + string.Join(" ", manifest.Problems));
        }

        EnsureOwnedOrEmpty();
        foreach (var file in ManagedFiles)
        {
            File.Delete(Path.Combine(StateDirectory, file));
        }

        WriteBaseline();
        return new ResetResult(StateDirectory, manifest.Version, ManagedFiles);
    }

    private void WriteBaseline()
    {
        Directory.CreateDirectory(StateDirectory);
        var header = CsvTable.ReadHeaderLine(Path.Combine(SeedDirectory, DecisionLogFile));
        File.WriteAllText(Path.Combine(StateDirectory, DecisionLogFile), header + "\n");
        File.WriteAllText(Path.Combine(StateDirectory, MarkerFile),
            "Local Cost-to-Serve demo state. Recreate with: ./scripts/reset-demo.sh\n");
    }

    private void EnsureOwnedOrEmpty()
    {
        if (Directory.Exists(StateDirectory)
            && !File.Exists(Path.Combine(StateDirectory, MarkerFile))
            && Directory.EnumerateFileSystemEntries(StateDirectory).Any())
        {
            throw new InvalidOperationException(
                $"'{StateDirectory}' is not empty and is not a demo state directory (no {MarkerFile}); refusing to modify it.");
        }
    }

    private static bool IsSameOrInside(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }
}
