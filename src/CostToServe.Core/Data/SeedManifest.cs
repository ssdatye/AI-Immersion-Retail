using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CostToServe.Core.Data;

public sealed record ManifestVerification(bool IsValid, string Version, IReadOnlyList<string> Problems);

/// <summary>
/// Verifies a directory against its <c>SHA256SUMS</c> manifest (shasum -a 256 format),
/// so demo baselines are provably unchanged before use or reset.
/// </summary>
public static partial class SeedManifest
{
    public const string FileName = "SHA256SUMS";

    public static ManifestVerification Verify(string directory)
    {
        var manifestPath = Path.Combine(directory, FileName);
        if (!File.Exists(manifestPath))
        {
            return new(false, "unknown", [$"Manifest '{FileName}' not found in {directory}."]);
        }

        var manifestBytes = File.ReadAllBytes(manifestPath);
        var version = Convert.ToHexStringLower(SHA256.HashData(manifestBytes))[..12];
        var problems = new List<string>();
        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (line, number) in File.ReadAllLines(manifestPath).Select((l, i) => (l, i + 1)))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = ManifestLine().Match(line);
            if (!match.Success)
            {
                problems.Add($"{FileName} line {number} is not '<sha256>  <file>'.");
                continue;
            }

            var file = match.Groups["file"].Value;
            if (file.Contains('/') || file.Contains('\\') || file == "..")
            {
                problems.Add($"{FileName} line {number} must name a file in the same directory.");
                continue;
            }

            listed.Add(file);
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                problems.Add($"{file} is listed but missing.");
                continue;
            }

            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, match.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{file} has changed (SHA-256 mismatch).");
            }
        }

        foreach (var csv in Directory.EnumerateFiles(directory, "*.csv").Select(Path.GetFileName).OfType<string>())
        {
            if (!listed.Contains(csv))
            {
                problems.Add($"{csv} is not listed in {FileName}.");
            }
        }

        return new(problems.Count == 0, version, problems);
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64}) [ *](?<file>.+)$")]
    private static partial Regex ManifestLine();
}
