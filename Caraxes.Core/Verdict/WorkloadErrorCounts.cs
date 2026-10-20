/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;

namespace Caraxes.Core.Verdict;

/// <summary>
/// The workload's <c>errors.json</c> reduced to the errors that carry a server verdict: per server code, a
/// count and one sample. <c>errors.json</c> also records operations with no verdict — an indeterminate
/// commit, a cancelled call, a transport cut the client stamps <c>CADB0000</c> — which a fault produces by
/// design; those are counted in <see cref="Unattributed"/> and never in <see cref="Total"/>. What is left
/// is what the application saw the server refuse without telling it to retry.
/// </summary>
public sealed record WorkloadErrorCounts(long Total, IReadOnlyDictionary<string, (long Count, string Sample)> ByCode, long Unattributed = 0)
{
    /// <summary>The client's code for a failure nothing answered (see the workload's ErrorClassifier).</summary>
    private const string NoVerdict = "CADB0000";

    public static WorkloadErrorCounts Read(string path)
    {
        if (!File.Exists(path))
            return new WorkloadErrorCounts(0, new Dictionary<string, (long, string)>());

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return Parse(doc.RootElement);
    }

    public static WorkloadErrorCounts Parse(JsonElement root)
    {
        long total = 0;
        long unattributed = 0;
        SortedDictionary<string, (long, string)> byCode = new(StringComparer.Ordinal);
        if (root.TryGetProperty("byCode", out JsonElement codes) && codes.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty code in codes.EnumerateObject())
            {
                foreach ((long count, string sample) in Classes(code.Value))
                {
                    if (code.Name == NoVerdict || !CarriesVerdict(sample))
                    {
                        unattributed += count;
                        continue;
                    }

                    total += count;
                    (long seen, string first) = byCode.GetValueOrDefault(code.Name, (0, sample));
                    byCode[code.Name] = (seen + count, first);
                }
            }
        }

        return new WorkloadErrorCounts(total, byCode, unattributed);
    }

    /// <summary>The per-class counts of one code, or the code's own count and first sample when the file
    /// predates the class breakdown.</summary>
    private static IEnumerable<(long Count, string Sample)> Classes(JsonElement code)
    {
        if (code.TryGetProperty("classes", out JsonElement classes) && classes.ValueKind == JsonValueKind.Array && classes.GetArrayLength() > 0)
        {
            foreach (JsonElement c in classes.EnumerateArray())
                yield return (Long(c, "count"), c.TryGetProperty("sample", out JsonElement s) ? s.GetString() ?? "" : "");
            yield break;
        }

        string sample = code.TryGetProperty("samples", out JsonElement samples) && samples.ValueKind == JsonValueKind.Array && samples.GetArrayLength() > 0
            ? samples[0].GetString() ?? ""
            : "";
        yield return (Long(code, "count"), sample);
    }

    /// <summary>A sample reads <c>Write:DomainError — …</c>: the operation, the workload's status, then the
    /// message. Domain and internal errors are server refusals; every other status is retried or ambiguous.</summary>
    private static bool CarriesVerdict(string sample)
        => sample.Contains(":DomainError", StringComparison.Ordinal) || sample.Contains(":InternalError", StringComparison.Ordinal);

    private static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
