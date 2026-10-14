/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Backup;

/// <summary>
/// The dataset reduced to four numbers: rows, <c>SUM(balance)</c>, <c>SUM(version)</c> and
/// <c>MAX(version)</c>, over every table of the workload's dataset. Two reads of one ledger agree on
/// all four; a restore that lost a tail of transfers keeps the balance sum (every transfer conserves
/// it) and loses on the version sum (every transfer raises it), which is why the sum of versions is
/// in here and the sum of balances alone would not do.
/// </summary>
public sealed record LedgerFingerprint(long Rows, long BalanceSum, long VersionSum, long MaxVersion)
{
    public string Describe() => $"rows {Rows:N0}, SUM(balance) {BalanceSum:N0}, SUM(version) {VersionSum:N0}, MAX(version) {MaxVersion:N0}";

    public static LedgerFingerprint operator +(LedgerFingerprint a, LedgerFingerprint b)
        => new(a.Rows + b.Rows, a.BalanceSum + b.BalanceSum, a.VersionSum + b.VersionSum, Math.Max(a.MaxVersion, b.MaxVersion));
}

/// <summary>Reads the ledger fingerprint over a node's REST port from the host (<c>/execute-sql-query</c>
/// is an ordinary query endpoint, not loopback-only).</summary>
public static class LedgerProbe
{
    public const string BaseTableName = "workload_accounts";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>The dataset's table names, as <c>CamusDB.Workload</c> lays them out: one table, or
    /// <c>workload_accounts_00..</c> when the rows are spread over several.</summary>
    public static IReadOnlyList<string> TableNames(int tables)
        => tables <= 1 ? [BaseTableName] : Enumerable.Range(0, tables).Select(i => $"{BaseTableName}_{i:D2}").ToList();

    public static async Task<LedgerFingerprint> ReadAsync(NodePlan node, string database, int tables, CancellationToken cancellationToken)
    {
        LedgerFingerprint total = new(0, 0, 0, 0);
        foreach (string table in TableNames(tables))
            total += await ReadTableAsync(node, database, table, cancellationToken).ConfigureAwait(false);
        return total;
    }

    private static async Task<LedgerFingerprint> ReadTableAsync(NodePlan node, string database, string table, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = Timeout };
        string sql = $"SELECT COUNT(*), SUM(balance), SUM(version), MAX(version) FROM {table}";
        using StringContent body = new(JsonSerializer.Serialize(new { databaseName = database, sql }), System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client
            .PostAsync($"http://localhost:{node.HostRestPort}/execute-sql-query", body, cancellationToken).ConfigureAwait(false);
        string answer = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return Parse(answer, node.Name, table);
    }

    /// <summary>Decodes the positional row set: <c>rows[0]</c> holds the four aggregates in order.</summary>
    public static LedgerFingerprint Parse(string answer, string node, string table)
    {
        using JsonDocument doc = JsonDocument.Parse(answer);
        JsonElement root = doc.RootElement;
        string status = root.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "" : "";
        if (status != "ok")
        {
            string code = root.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "" : "";
            string message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
            throw new InvalidOperationException($"ledger read of {table} on {node} failed: {code} {message}".Trim());
        }

        if (!root.TryGetProperty("rows", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 1)
            throw new InvalidOperationException($"ledger read of {table} on {node} returned no aggregate row");

        JsonElement row = rows[0];
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 4)
            throw new InvalidOperationException($"ledger read of {table} on {node} returned {row.GetArrayLength()} column(s), expected 4");

        return new LedgerFingerprint(Number(row[0]), Number(row[1]), Number(row[2]), Number(row[3]));
    }

    private static long Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetInt64(),
        JsonValueKind.String => long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.Null => 0,
        _ => throw new InvalidOperationException($"unexpected aggregate value {value}"),
    };
}
