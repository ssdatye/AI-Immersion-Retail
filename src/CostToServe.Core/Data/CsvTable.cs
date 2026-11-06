using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace CostToServe.Core.Data;

/// <summary>
/// One seed table as raw text: the CSV header and fields exactly as stored. CSV files and the
/// Azure SQL copy (text columns) both produce this shape, so parsing and validation are shared
/// and the engine sees identical values from either source.
/// </summary>
public sealed record SeedTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows);

/// <summary>Minimal RFC 4180 CSV reader with header validation and invariant-culture parsing.</summary>
internal static class CsvTable
{
    public static IReadOnlyList<CsvRow> Read(string path, params string[] requiredColumns) =>
        Rows(ReadRaw(path), requiredColumns);

    /// <summary>Reads a CSV file as raw text, skipping blank rows and checking the field count.</summary>
    public static SeedTable ReadRaw(string path)
    {
        var file = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Seed file '{file}' not found.", path);
        }

        using var parser = new TextFieldParser(path) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");

        var header = parser.ReadFields() ?? throw new InvalidDataException($"{file} is empty.");
        var rows = new List<string[]>();
        while (!parser.EndOfData)
        {
            var line = parser.LineNumber;
            var fields = parser.ReadFields();
            if (fields is null || fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (fields.Length != header.Length)
            {
                throw new InvalidDataException($"{file} line {line}: expected {header.Length} fields, found {fields.Length}.");
            }

            rows.Add(fields);
        }

        return new SeedTable(file, header, rows);
    }

    /// <summary>Validates required columns and wraps each row for typed access.</summary>
    public static IReadOnlyList<CsvRow> Rows(SeedTable table, params string[] requiredColumns)
    {
        var columns = table.Columns.Select((name, index) => (name, index)).ToDictionary(c => c.name, c => c.index, StringComparer.Ordinal);
        var missing = requiredColumns.Where(c => !columns.ContainsKey(c)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"{table.Name} is missing column(s): {string.Join(", ", missing)}.");
        }

        return table.Rows.Select((fields, i) => fields.Length == table.Columns.Count
                ? new CsvRow(table.Name, i + 2, columns, fields)
                : throw new InvalidDataException($"{table.Name} row {i + 1}: expected {table.Columns.Count} fields, found {fields.Length}."))
            .ToArray();
    }

    public static string ReadHeaderLine(string path) =>
        File.ReadLines(path).FirstOrDefault() ?? throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
}

internal sealed class CsvRow(string file, long line, IReadOnlyDictionary<string, int> columns, string[] fields)
{
    public string Text(string column) => fields[columns[column]];

    public double Number(string column) =>
        double.TryParse(Text(column), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw Invalid(column, "a number");

    public DateOnly Date(string column) =>
        DateOnly.TryParseExact(Text(column), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : throw Invalid(column, "a yyyy-MM-dd date");

    public InvalidDataException Invalid(string column, string expected) =>
        new($"{file} line {line}: column '{column}' value '{Text(column)}' is not {expected}.");
}
