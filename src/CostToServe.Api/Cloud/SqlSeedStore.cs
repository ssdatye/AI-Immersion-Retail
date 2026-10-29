using System.Data;
using System.Text.RegularExpressions;
using Azure.Core;
using CostToServe.Core.Data;
using Microsoft.Data.SqlClient;

namespace CostToServe.Api.Cloud;

/// <summary>
/// The seed tables in Azure SQL, schema <c>seed</c>. Each CSV is one table with a <c>row_num</c>
/// key and one NVARCHAR column per CSV column, holding the CSV text unchanged, so the engine
/// parses identical values in CSV and Azure modes. <c>seed.manifest</c> records the seed
/// version. Connections use Entra tokens from the caller's credential; there is no password.
/// </summary>
public static partial class SqlSeedStore
{
    public const string Schema = "seed";

    public static SqlConnection Connect(SqlOptions options, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("CostToServe:Sql:ConnectionString is not configured.");
        }

        var builder = new SqlConnectionStringBuilder(options.ConnectionString);
        if (!string.IsNullOrEmpty(builder.Password) || !string.IsNullOrEmpty(builder.UserID))
        {
            throw new InvalidOperationException("The SQL connection string must not contain a user or password; Entra tokens are used.");
        }

        return new SqlConnection(builder.ConnectionString)
        {
            AccessTokenCallback = async (_, ct) =>
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([options.TokenScope]), ct).ConfigureAwait(false);
                return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
            },
        };
    }

    /// <summary>Reads every seed table and the seed version.</summary>
    public static async Task<(IReadOnlyDictionary<string, SeedTable> Tables, string Version)> LoadAsync(
        SqlOptions options, TokenCredential credential, CancellationToken ct = default)
    {
        await using var connection = Connect(options, credential);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string version;
        await using (var cmd = new SqlCommand($"SELECT TOP 1 version FROM [{Schema}].[manifest] ORDER BY loaded_at DESC", connection))
        {
            version = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException("Azure SQL has no seed data. Run ./scripts/seed-azure-sql.sh.");
        }

        var tables = new Dictionary<string, SeedTable>(StringComparer.Ordinal);
        foreach (var file in SeedDataSet.TableFiles)
        {
            await using var cmd = new SqlCommand($"SELECT * FROM [{Schema}].[{TableName(file)}] ORDER BY row_num", connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var columns = Enumerable.Range(1, reader.FieldCount - 1).Select(reader.GetName).ToArray();
            var rows = new List<string[]>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(columns.Select((_, i) => reader.GetString(i + 1)).ToArray());
            }

            tables[file] = new SeedTable(file, columns, rows);
        }

        return (tables, version);
    }

    /// <summary>
    /// Replaces the seed tables with the given CSV tables and grants the app's identity read access.
    /// Destructive for the demo database's seed schema only.
    /// </summary>
    public static async Task SeedAsync(
        SqlOptions options, TokenCredential credential, IReadOnlyDictionary<string, SeedTable> tables, string version,
        string? readerIdentityName, TextWriter log, CancellationToken ct = default)
    {
        await using var connection = Connect(options, credential);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await ExecAsync(connection, $"IF SCHEMA_ID('{Schema}') IS NULL EXEC('CREATE SCHEMA [{Schema}]')", ct);
        foreach (var file in SeedDataSet.TableFiles)
        {
            var table = tables[file];
            var name = TableName(file);
            foreach (var column in table.Columns)
            {
                EnsureIdentifier(column);
            }

            await ExecAsync(connection,
                $"DROP TABLE IF EXISTS [{Schema}].[{name}]; CREATE TABLE [{Schema}].[{name}] (row_num INT NOT NULL PRIMARY KEY, "
                + string.Join(", ", table.Columns.Select(c => $"[{c}] NVARCHAR(400) NOT NULL")) + ")", ct);

            using var data = new DataTable();
            data.Columns.Add("row_num", typeof(int));
            foreach (var column in table.Columns)
            {
                data.Columns.Add(column, typeof(string));
            }

            for (var i = 0; i < table.Rows.Count; i++)
            {
                data.Rows.Add([i + 1, .. table.Rows[i]]);
            }

            using var bulk = new SqlBulkCopy(connection) { DestinationTableName = $"[{Schema}].[{name}]" };
            foreach (DataColumn column in data.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulk.WriteToServerAsync(data, ct).ConfigureAwait(false);
            log.WriteLine($"  {Schema}.{name}: {table.Rows.Count} rows");
        }

        await ExecAsync(connection,
            $"IF OBJECT_ID('[{Schema}].[manifest]') IS NULL CREATE TABLE [{Schema}].[manifest] (version NVARCHAR(64) NOT NULL, loaded_at DATETIME2 NOT NULL)", ct);
        await using (var cmd = new SqlCommand($"INSERT INTO [{Schema}].[manifest] (version, loaded_at) VALUES (@v, SYSUTCDATETIME())", connection))
        {
            cmd.Parameters.AddWithValue("@v", version);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(readerIdentityName))
        {
            EnsureIdentifier(readerIdentityName, allowHyphen: true);
            await ExecAsync(connection,
                $"IF DATABASE_PRINCIPAL_ID('{readerIdentityName}') IS NULL CREATE USER [{readerIdentityName}] FROM EXTERNAL PROVIDER; "
                + $"GRANT SELECT ON SCHEMA::[{Schema}] TO [{readerIdentityName}];", ct);
            log.WriteLine($"  granted read on schema {Schema} to {readerIdentityName}");
        }
    }

    public static string TableName(string file) => Path.GetFileNameWithoutExtension(file);

    private static async Task ExecAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Identifiers are interpolated into DDL, so only plain names are accepted.</summary>
    private static void EnsureIdentifier(string name, bool allowHyphen = false)
    {
        if (!(allowHyphen ? IdentityName() : ColumnName()).IsMatch(name))
        {
            throw new InvalidDataException($"'{name}' is not a plain identifier.");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex ColumnName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,127}$")]
    private static partial Regex IdentityName();
}
