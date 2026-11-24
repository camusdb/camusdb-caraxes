/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text;
using System.Text.Json;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Schema;

/// <summary>
/// One explicit transaction on one node, over the REST port: <c>/start-transaction</c>, statements that
/// carry its handle, and <c>/commit-transaction</c> or <c>/rollback-transaction</c>. As in
/// <see cref="SqlRest"/>, a refusal comes back as a non-ok answer and a transport failure throws.
/// </summary>
public sealed class SqlSession
{
    private readonly NodePlan node;

    private readonly string database;

    private readonly TimeSpan timeout;

    private readonly long idPhysical;

    private readonly uint idCounter;

    private SqlSession(NodePlan node, string database, TimeSpan timeout, long idPhysical, uint idCounter)
    {
        this.node = node;
        this.database = database;
        this.timeout = timeout;
        this.idPhysical = idPhysical;
        this.idCounter = idCounter;
    }

    public static async Task<SqlSession> StartAsync(NodePlan node, string database, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string answer = await PostAsync(node, "/start-transaction", new { databaseName = database }, timeout, cancellationToken).ConfigureAwait(false);

        using JsonDocument doc = JsonDocument.Parse(answer);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("status", out JsonElement status) || status.GetString() != "ok")
            throw new InvalidOperationException($"{node.Name} did not start a transaction: {answer}");

        return new SqlSession(node, database, timeout, root.GetProperty("txnIdPT").GetInt64(), root.GetProperty("txnIdCounter").GetUInt32());
    }

    public async Task<SqlAnswer> ExecuteAsync(string sql, CancellationToken cancellationToken) =>
        SqlRest.Parse(await PostAsync(
            node, SqlRest.RouteFor(sql), new { databaseName = database, sql, txnIdPT = idPhysical, txnIdCounter = idCounter },
            timeout, cancellationToken).ConfigureAwait(false));

    public Task<SqlAnswer> CommitAsync(CancellationToken cancellationToken) => EndAsync("/commit-transaction", cancellationToken);

    public Task<SqlAnswer> RollbackAsync(CancellationToken cancellationToken) => EndAsync("/rollback-transaction", cancellationToken);

    private async Task<SqlAnswer> EndAsync(string route, CancellationToken cancellationToken) =>
        SqlRest.Parse(await PostAsync(
            node, route, new { databaseName = database, txnIdPT = idPhysical, txnIdCounter = idCounter }, timeout, cancellationToken).ConfigureAwait(false));

    private static async Task<string> PostAsync(NodePlan node, string route, object body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = timeout };
        using StringContent content = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client
            .PostAsync($"http://localhost:{node.HostRestPort}{route}", content, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
