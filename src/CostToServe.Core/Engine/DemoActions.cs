using System.Globalization;
using CostToServe.Core.Data;
using CostToServe.Core.Model;

namespace CostToServe.Core.Engine;

/// <summary>
/// The one numeric lever a decision may adjust for an action (for example A6's pickup shift).
/// Values are fractions; <see cref="Default"/> is the playbook setting.
/// </summary>
public sealed record ActionSettingSpec(string Lever, string Label, double Default, double Min, double Max, double Step)
{
    public string Format(double value) => $"{Label} {(Lever == "split" ? "-" : string.Empty)}{(value * 100).ToString("0.#", CultureInfo.InvariantCulture)}%";
}

/// <summary>An approved playbook action and the engine levers that model it.</summary>
public sealed record DemoAction(string Id, Levers Levers, string Source)
{
    /// <summary>True when the settings are not from the reference demo but derived from playbook text.</summary>
    public bool LeversDerivedFromPlaybookText => Source.StartsWith("derived", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The adjustable setting: the action's single non-zero share lever (batch, closer, split or pickup).
    /// Null for on/off actions (A3, A5) and for actions that set several levers or a fee.
    /// </summary>
    public ActionSettingSpec? Setting
    {
        get
        {
            var set = new (string Lever, string Label, double Value, double Max)[]
            {
                ("batch", "Batch Express", Levers.Batch, LeverLimits.BatchMax),
                ("closer", "Fill from a closer store", Levers.Closer, LeverLimits.CloserMax),
                ("split", "Split shipments", Levers.Split, LeverLimits.SplitMax),
                ("pickup", "Shift to Pickup", Levers.Pickup, LeverLimits.PickupMax),
            }.Where(l => l.Value > 0).ToArray();
            return set.Length == 1 && Levers.Fee == 0 && !Levers.Basket && !Levers.Cap
                ? new ActionSettingSpec(set[0].Lever, set[0].Label, set[0].Value, 0.01, set[0].Max, 0.01)
                : null;
        }
    }

    /// <summary>The action's levers with its adjustable setting replaced. Null keeps the playbook setting.</summary>
    public Levers WithSetting(double? value)
    {
        if (value is null)
        {
            return Levers;
        }

        var spec = Setting ?? throw new ArgumentException($"{Id} has no adjustable setting; it is applied as defined in the playbook.");
        var v = Math.Round(value.Value, 4);
        if (!double.IsFinite(v) || v < spec.Min || v > spec.Max)
        {
            throw new ArgumentException($"{Id} setting must be between {spec.Min * 100:0.#}% and {spec.Max * 100:0.#}% (as a fraction {spec.Min} to {spec.Max}).");
        }

        return spec.Lever switch
        {
            "batch" => Levers with { Batch = v },
            "closer" => Levers with { Closer = v },
            "split" => Levers with { Split = v },
            _ => Levers with { Pickup = v },
        };
    }
}

/// <summary>
/// Lever settings that model each playbook action (data/seed/action_playbook.csv). Loaded from
/// data/config/action-levers.csv so they can be changed without code. A1-A3 come from the
/// reference demo's ACT list; A4-A6 have no reference settings and are derived from the
/// playbook's effect text.
/// </summary>
public sealed class ActionCatalog
{
    private ActionCatalog(IReadOnlyList<DemoAction> all) => All = all;

    public IReadOnlyList<DemoAction> All { get; }

    /// <summary>Built-in values, identical to the shipped config file; used when no file is configured.</summary>
    public static ActionCatalog Default { get; } = new(
    [
        new("A1", new Levers { Batch = 0.35 }, "reference demo (ACT list)"),
        new("A2", new Levers { Closer = 0.2 }, "reference demo (ACT list)"),
        new("A3", new Levers { Basket = true }, "reference demo (ACT list)"),
        new("A4", new Levers { Split = 0.4 }, "derived from playbook effect text (-40% split-shipment rate)"),
        new("A5", new Levers { Cap = true }, "derived from playbook effect text (surcharge capped at contract index)"),
        new("A6", new Levers { Pickup = 0.03 }, "derived from playbook effect text (shift 3% to Pickup)"),
    ]);

    public DemoAction? Find(string? id) =>
        All.FirstOrDefault(a => string.Equals(a.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads and validates the catalog. Lever values must be within the What-If limits.</summary>
    public static ActionCatalog Load(string path)
    {
        var rows = CsvTable.Read(path, "action_id", "batch", "closer", "split", "pickup", "fee", "basket", "cap", "source");
        var actions = new List<DemoAction>();
        foreach (var r in rows)
        {
            var id = r.Text("action_id").Trim().ToUpperInvariant();
            if (actions.Any(a => a.Id == id))
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}: duplicate action '{id}'.");
            }

            var levers = new Levers
            {
                Batch = r.Number("batch"),
                Closer = r.Number("closer"),
                Split = r.Number("split"),
                Pickup = r.Number("pickup"),
                Fee = r.Number("fee"),
                Basket = Bool(r, "basket"),
                Cap = Bool(r, "cap"),
            };
            try
            {
                levers.Validate();
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}: action '{id}': {ex.Message.Split('\n')[0]}");
            }

            actions.Add(new DemoAction(id, levers, r.Text("source").Trim()));
        }

        return new ActionCatalog(actions);

        static bool Bool(CsvRow row, string column) =>
            bool.TryParse(row.Text(column), out var value) ? value : throw row.Invalid(column, "true or false");
    }

    /// <summary>Loads the file when it exists; otherwise returns the built-in defaults.</summary>
    public static ActionCatalog LoadOrDefault(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Load(path) : Default;

    public override string ToString() => string.Join(", ", All.Select(a => a.Id.ToString(CultureInfo.InvariantCulture)));
}
