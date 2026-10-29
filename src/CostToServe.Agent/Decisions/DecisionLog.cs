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
public sealed record DecisionEntry(DateTimeOffset Timestamp, string ActionId, string Actor, DecisionKind Decision, string Reason, double? Setting = null);

/// <summary>A decision awaiting human confirmation in the host (terminal prompt or UI card).</summary>
public sealed record DecisionProposal(
    string ActionId,
    string ActionName,
    DecisionKind Decision,
    string Actor,
    string Reason,
    double? EstimatedWeeklySavingUsd,
    IReadOnlyList<string> GuardrailNotes,
    string? Setting = null);

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

            EnsureSettingColumn();
            var line = string.Join(',',
                entry.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Escape(entry.ActionId),
                Escape(entry.Actor),
                entry.Decision.ToString().ToLowerInvariant(),
                Escape(entry.Reason),
                string.Empty,
                entry.Setting?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            File.AppendAllText(Path, line + "\n");
        }
    }

    private void EnsureSettingColumn()
    {
        var lines = File.ReadAllLines(Path);
        if (lines.Length == 0 || lines[0].Split(',').Contains(SettingColumn))
        {
            return;
        }

        lines[0] += "," + SettingColumn;
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
                : null);
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
