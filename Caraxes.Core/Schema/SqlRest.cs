/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Schema;

/// <summary>
/// A node's answer to one SQL statement: the envelope's status, code and message, the affected-row
/// count of a DDL or DML statement, and the positional row set of a query (each value cloned out of
/// the response document, so it outlives it).
/// </summary>
public sealed record SqlAnswer(
    bool Ok,
    string Code,
    string Message,
    int AffectedRows,
    IReadOnlyList<string> Columns,
    IReadOnlyList<JsonElement[]> Rows)
{
    public string Describe() => Ok ? "ok" : $"{Code} {Message}".Trim();
}

/// <summary>
/// Sends SQL to a node's REST port from the host. The three SQL routes are ordinary endpoints, not
/// loopback-only, so no in-container hop is needed (unlike the topology and backup endpoints). Each
/// statement goes to the node it names and runs against that node's catalog, which is what a
/// per-node schema check needs: CamusDB's schema is node-local in memory, so a node that missed a
/// delta answers differently from its peers.
/// </summary>
public static class SqlRest
{
    /// <summary>The route a statement takes: <c>/execute-sql-ddl</c> for schema statements,
    /// <c>/execute-sql-non-query</c> for writes, <c>/execute-sql-query</c> for everything that returns
    /// rows. Chosen by the leading keyword, because the endpoints refuse each other's statements.</summary>
    public static string RouteFor(string sql)
    {
        string keyword = LeadingKeyword(sql);
        return keyword switch
        {
            "CREATE" or "ALTER" or "DROP" or "RENAME" or "TRUNCATE" or "COMMENT" or "ANALYZE" => "/execute-sql-ddl",
            "INSERT" or "UPDATE" or "DELETE" or "UPSERT" or "REPLACE" => "/execute-sql-non-query",
            _ => "/execute-sql-query",
        };
    }

    private static string LeadingKeyword(string sql)
    {
        string trimmed = sql.TrimStart();
        int end = 0;
        while (end < trimmed.Length && char.IsLetter(trimmed[end]))
            end++;
        return trimmed[..end].ToUpperInvariant();
    }

    /// <summary>
    /// Runs <paramref name="sql"/> on <paramref name="node"/> and returns the node's answer. A refused
    /// statement comes back as a non-ok answer, not an exception; a transport failure (the node died,
    /// the request outlived <paramref name="timeout"/>) throws, because then nothing answered.
    /// </summary>
    public static async Task<SqlAnswer> ExecuteAsync(
        NodePlan node, string database, string sql, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = timeout };
        using StringContent body = new(
            JsonSerializer.Serialize(new { databaseName = database, sql }), System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client
            .PostAsync($"http://localhost:{node.HostRestPort}{RouteFor(sql)}", body, cancellationToken).ConfigureAwait(false);
        string answer = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return Parse(answer);
    }

    /// <summary>Decodes any of the three envelopes: <c>status</c>, <c>code</c>, <c>message</c>, and
    /// either a row count (<c>rows</c> as a number) or a positional row set (<c>columns</c> plus
    /// <c>rows</c> as an array of arrays).</summary>
    public static SqlAnswer Parse(string answer)
    {
        using JsonDocument doc = JsonDocument.Parse(answer);
        JsonElement root = doc.RootElement;

        string status = Text(root, "status");
        string code = Text(root, "code");
        string message = Text(root, "message");

        List<string> columns = [];
        if (root.TryGetProperty("columns", out JsonElement cols) && cols.ValueKind == JsonValueKind.Array)
            foreach (JsonElement col in cols.EnumerateArray())
                columns.Add(Text(col, "name"));

        int affected = 0;
        List<JsonElement[]> rows = [];
        if (root.TryGetProperty("rows", out JsonElement r))
        {
            if (r.ValueKind == JsonValueKind.Number)
                affected = r.GetInt32();
            else if (r.ValueKind == JsonValueKind.Array)
                foreach (JsonElement row in r.EnumerateArray())
                    rows.Add(row.ValueKind == JsonValueKind.Array
                        ? row.EnumerateArray().Select(v => v.Clone()).ToArray()
                        : [row.Clone()]);
        }

        return new SqlAnswer(status == "ok", code, message, affected, columns, rows);
    }

    private static string Text(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>A value as one comparable string: the string itself for a JSON string, the raw
    /// literal for a number or a bool, <c>NULL</c> for null. Two reads of one value agree on it
    /// whatever its type, which is all a cross-read comparison needs.</summary>
    public static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "NULL",
        _ => value.GetRawText(),
    };

    /// <summary>A value as a SQL literal, for a point probe's WHERE clause: numbers and bools as they
    /// are, strings quoted with embedded quotes doubled. Null has no equality literal.</summary>
    public static string? Literal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => $"'{value.GetString()!.Replace("'", "''")}'",
        _ => null,
    };
}
