using System.Text;
using CostToServe.Core.State;

namespace CostToServe.Agent.Decisions;

public enum DecisionKind
{
    Propose,
    Approve,
    Reject,
    Execute,
}

/// <param name="Setting">The adjustable lever value decided with this step (fraction), or null when the action has none.</param>
/// <param name="Region">
/// The region the decision applies to. Each action is decided separately per region. Null only for rows
/// logged before decisions had regions; those count as <see cref="DecisionRegions.Legacy"/>.
/// </param>
public sealed record DecisionEntry(
    DateTimeOffset Timestamp, string ActionId, string Actor, DecisionKind Decision, string Reason, double? Setting = null, string? Region = null)
{
    public string EffectiveRegion => string.IsNullOrWhiteSpace(Region) ? DecisionRegions.Legacy : Region;
}

public static class DecisionRegions
{
    /// <summary>Region for rows logged before decisions were per region (the demo's only ops region).</summary>
    public const string Legacy = "Southeast";
}

/// <summary>A decision awaiting human confirmation in the host (terminal prompt or UI card).</summary>
public sealed record DecisionProposal(
    string ActionId,
    string ActionName,
    DecisionKind Decision,
    string Actor,
    string Reason,
    double? EstimatedWeeklySavingUsd,
    IReadOnlyList<string> GuardrailNotes,
    string? Setting = null,
    string? Region = null);

/// <summary>Asks the human to confirm a decision. The model can request; only the human commits.</summary>
public interface IDecisionConfirmer
{
    Task<bool> ConfirmAsync(DecisionProposal proposal, CancellationToken cancellationToken);
}

public interface IDecisionLog
{
    IReadOnlyList<DecisionEntry> ReadAll();

    void Append(DecisionEntry entry);
}

/// <summary>
/// CSV-mode decision log in the local demo state (data/local/decision_log.csv),
/// using the seed schema plus a trailing <c>setting</c> column. The <c>approver</c> column
/// records the acting person. Logs created from the seed template gain the column on the
/// first write; earlier rows read as having no setting.
/// </summary>
public sealed class CsvDecisionLog(string stateDirectory) : IDecisionLog
{
    public const string SettingColumn = "setting";
    public const string RegionColumn = "region";

    private readonly Lock _gate = new();

    public string Path { get; } = System.IO.Path.Combine(stateDirectory, LocalDemoState.DecisionLogFile);

    public IReadOnlyList<DecisionEntry> ReadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            return File.ReadLines(Path).Skip(1)
                .Where(l => !string.IsNullOrWhiteSpace(l.Replace(",", string.Empty)))
                .Select(ParseLine)
                .ToArray();
        }
    }

    public void Append(DecisionEntry entry)
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                throw new InvalidOperationException($"Decision log not found at {Path}. Run ./scripts/reset-demo.sh to initialize demo state.");
            }

            EnsureColumns();
            var line = string.Join(',',
                entry.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Escape(entry.ActionId),
                Escape(entry.Actor),
                entry.Decision.ToString().ToLowerInvariant(),
                Escape(entry.Reason),
                string.Empty,
                entry.Setting?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(entry.Region ?? string.Empty));
            File.AppendAllText(Path, line + "\n");
        }
    }

    /// <summary>Adds the setting and region columns to a log created from the seed template.</summary>
    private void EnsureColumns()
    {
        var lines = File.ReadAllLines(Path);
        if (lines.Length == 0)
        {
            return;
        }

        var header = lines[0].Split(',');
        var missing = new[] { SettingColumn, RegionColumn }.Where(c => !header.Contains(c)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        lines[0] += "," + string.Join(',', missing);
        File.WriteAllText(Path, string.Join("\n", lines) + "\n");
    }

    internal static string Escape(string value)
    {
        var flat = value.Replace("\r", " ").Replace("\n", " ");
        return flat.IndexOfAny([',', '"']) >= 0 ? "\"" + flat.Replace("\"", "\"\"") + "\"" : flat;
    }

    private static DecisionEntry ParseLine(string line)
    {
        var fields = SplitCsv(line);
        return new DecisionEntry(
            DateTimeOffset.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture),
            fields[1],
            fields[2],
            Enum.Parse<DecisionKind>(fields[3], ignoreCase: true),
            fields.Count > 4 ? fields[4] : string.Empty,
            fields.Count > 6 && double.TryParse(fields[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var setting)
                ? setting
                : null,
            fields.Count > 7 && fields[7].Length > 0 ? fields[7] : null);
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
