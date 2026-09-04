using System.Globalization;
using System.Text;
using System.Text.Json;

// memdecomp — splits a Caraxes run's per-node memory growth into components, from artifacts
// the run already collected. No engine, no docker, no gcdump needed.
//
// Usage:
//   memdecomp <runDir> [--bucket <seconds>] [--csv]
//
//   <runDir> is a scenario run directory, e.g. runs/scenarios/bank-soak-p1-w128-s1. It must hold
//   memory-samples.csv (docker stats, written by Caraxes NodeMonitor) and artifacts/run/ with
//   node-metrics.csv, run-meta.json and intervals.csv (written by CamusDB.Workload).
//
// What the columns mean, per node and per time bucket inside the measured window:
//   docker     docker stats MemUsage. On Linux this is cgroup memory usage MINUS inactive file
//              cache, so it still counts ACTIVE page cache, kernel slab and tmpfs. It is the
//              figure every Vorpal table in the campaign calls "RSS" or "peak memory".
//   ws         the node process working set, from the .NET runtime meter. Process RSS only.
//   committed  managed heap memory the GC has committed (live + free + fragmentation).
//   heap       managed heap size at the last GC (live + fragmentation, all generations).
//   frag       fragmentation inside that heap at the last GC.
//   cache+k    docker - ws: page cache, kernel and anything not the process. If THIS grows,
//              the growth is not in the process and no gcdump will find it.
//   native     ws - committed: RocksDB, WAL buffers, GC bookkeeping, thread stacks, JIT code.
//   gc_slack   committed - heap: committed regions the GC holds but does not use.
//   live       heap - frag: the best estimate of live managed data without a gcdump.
//
// Every quantity is placed on the node-metrics scrape timeline. The docker sample nearest to each
// scrape instant (within DockerMatchToleranceMs) is used, so derived columns subtract values taken
// at the same moment. The last block prints each component's growth per completed operation
// (KiB/op) between the first and the last scrape inside the window, divided by the operations
// completed between those two instants — the same shape as the 0.36–0.42 KiB/op the soaks report.

const long DockerMatchToleranceMs = 30_000;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 1;
}

string runDir = args[0];
int bucketSeconds = 300;
bool emitCsv = false;

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--bucket" when i + 1 < args.Length && int.TryParse(args[i + 1], out int b) && b > 0:
            bucketSeconds = b;
            i++;
            break;
        case "--csv":
            emitCsv = true;
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
}

string artifacts = Path.Combine(runDir, "artifacts", "run");
string metaPath = Path.Combine(artifacts, "run-meta.json");
string nodeMetricsPath = Path.Combine(artifacts, "node-metrics.csv");
string intervalsPath = Path.Combine(artifacts, "intervals.csv");
string memorySamplesPath = Path.Combine(runDir, "memory-samples.csv");

foreach (string required in new[] { metaPath, nodeMetricsPath })
{
    if (!File.Exists(required))
    {
        Console.Error.WriteLine($"missing required artifact: {required}");
        return 2;
    }
}

(DateTime measureStart, double measureSeconds) = ReadWindow(metaPath);
long measureStartMs = new DateTimeOffset(measureStart).ToUnixTimeMilliseconds();
int bucketCount = (int)Math.Ceiling(measureSeconds / bucketSeconds);

Console.WriteLine($"# Memory decomposition — {Path.GetFileName(Path.GetFullPath(runDir).TrimEnd(Path.DirectorySeparatorChar))}");
Console.WriteLine();
Console.WriteLine($"Measured window: {measureStart:O} for {measureSeconds:0} s, {bucketCount} bucket(s) of {bucketSeconds} s.");
Console.WriteLine();

// ---- node-metrics.csv

List<MetricPoint> points = ReadNodeMetrics(nodeMetricsPath);
if (points.Count == 0)
{
    Console.Error.WriteLine("node-metrics.csv holds no samples");
    return 2;
}

List<string> nodes = points.Select(p => p.Node).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
HashSet<string> families = points.Select(p => p.Metric).ToHashSet(StringComparer.Ordinal);

// Each quantity is looked up by stem, newest naming first. The Prometheus exporter appends a unit
// suffix (_bytes, _seconds_total) that depends on the meter's unit annotation, so a stem match is
// more robust than an exact name. The second stem in each list is the pre-.NET 9 runtime
// instrumentation name, kept so an older image still decomposes.
string? wsFamily = Resolve(families, "dotnet_process_memory_working_set", "process_memory_working_set", "process_working_set");
string? committedFamily = Resolve(families, "dotnet_gc_last_collection_memory_committed_size", "process_runtime_dotnet_gc_committed_memory_size");
string? heapFamily = Resolve(families, "dotnet_gc_last_collection_heap_size", "process_runtime_dotnet_gc_heap_size");
string? fragFamily = Resolve(families, "dotnet_gc_last_collection_heap_fragmentation_size", "process_runtime_dotnet_gc_heap_fragmentation_size");
string? allocFamily = Resolve(families, "dotnet_gc_heap_total_allocated", "process_runtime_dotnet_gc_allocations_size");
string? collectionsFamily = Resolve(families, "dotnet_gc_collections", "process_runtime_dotnet_gc_collections_count");
string? pauseFamily = Resolve(families, "dotnet_gc_pause_time");

Console.WriteLine("Metric families used (n/a means the run did not export one):");
Console.WriteLine();
Console.WriteLine("| quantity | family |");
Console.WriteLine("|---|---|");
Console.WriteLine($"| working set | {wsFamily ?? "n/a"} |");
Console.WriteLine($"| GC committed | {committedFamily ?? "n/a"} |");
Console.WriteLine($"| GC heap size | {heapFamily ?? "n/a"} |");
Console.WriteLine($"| GC fragmentation | {fragFamily ?? "n/a"} |");
Console.WriteLine($"| allocated bytes | {allocFamily ?? "n/a"} |");
Console.WriteLine($"| GC collections | {collectionsFamily ?? "n/a"} |");
Console.WriteLine($"| GC pause time | {pauseFamily ?? "n/a"} |");
Console.WriteLine();

if (wsFamily is null && committedFamily is null)
{
    Console.WriteLine("Neither a working-set nor a GC-committed series is present, so the process side cannot be");
    Console.WriteLine("separated from the cgroup figure. Check a raw metrics-<node>.txt scrape for the runtime meter.");
    Console.WriteLine();
}

// ---- memory-samples.csv (docker stats), keyed by container; mapped to a node by name suffix

Dictionary<string, List<(long Ms, double Mib, double LimitMib)>> dockerByNode = [];
if (File.Exists(memorySamplesPath))
{
    foreach ((DateTime ts, string container, double mib, double limit) in ReadMemorySamples(memorySamplesPath))
    {
        string? node = nodes.FirstOrDefault(n => container.EndsWith("-" + n, StringComparison.Ordinal) || container == n);
        if (node is null)
            continue;
        if (!dockerByNode.TryGetValue(node, out var list))
            dockerByNode[node] = list = [];
        list.Add((new DateTimeOffset(ts).ToUnixTimeMilliseconds(), mib, limit));
    }
    foreach (var list in dockerByNode.Values)
        list.Sort((a, b) => a.Ms.CompareTo(b.Ms));
}
else
{
    Console.WriteLine($"No memory-samples.csv at {memorySamplesPath}; the docker column will read n/a.");
    Console.WriteLine();
}

// ---- intervals.csv: completed operations per second, second 0 == measureStart

int windowSeconds = (int)Math.Ceiling(measureSeconds);
long[] completedPerSecond = new long[windowSeconds];
bool haveIntervals = false;
if (File.Exists(intervalsPath))
{
    foreach ((int second, long completed) in ReadIntervals(intervalsPath))
    {
        haveIntervals = true;
        if (second >= 0 && second < windowSeconds)
            completedPerSecond[second] += completed;
    }
}
if (!haveIntervals)
{
    Console.WriteLine($"No usable intervals.csv at {intervalsPath}; ops/s and per-operation growth read n/a.");
    Console.WriteLine();
}

double[] completedPerBucket = new double[bucketCount];
double[] cumulativeOps = new double[bucketCount];
for (int s = 0; s < windowSeconds; s++)
    completedPerBucket[s / bucketSeconds] += completedPerSecond[s];
for (int b = 0, running = 0; b < bucketCount; b++)
{
    running += (int)completedPerBucket[b];
    cumulativeOps[b] = running;
}

// ---- per node

StringBuilder csv = new();
csv.AppendLine("node,bucket,minutes_from,minutes_to,ops_per_s,cumulative_ops,docker_mib,ws_mib,committed_mib,heap_mib,frag_mib,cache_plus_kernel_mib,native_mib,gc_slack_mib,live_mib,gen2_collections,gc_pause_s,alloc_mb_per_s");

foreach (string node in nodes)
{
    List<MetricPoint> mine = points.Where(p => p.Node == node).ToList();
    List<Snapshot> snaps = BuildSnapshots(mine, dockerByNode.GetValueOrDefault(node));

    double?[] gen2 = CounterDeltaPerBucket(mine, collectionsFamily, ("gc_heap_generation", "gen2"), ("generation", "gen2"));
    double?[] pause = CounterDeltaPerBucket(mine, pauseFamily, null, null);
    double?[] alloc = CounterDeltaPerBucket(mine, allocFamily, null, null);
    double? dockerLimit = dockerByNode.TryGetValue(node, out var samples) && samples.Count > 0 ? samples[^1].LimitMib : null;

    Console.WriteLine($"## {node}" + (dockerLimit is { } lim && lim > 0 ? $" — container limit {lim:0} MiB" : ""));
    Console.WriteLine();
    if (snaps.Count == 0)
    {
        Console.WriteLine("No scrape inside the measured window for this node.");
        Console.WriteLine();
        continue;
    }

    Console.WriteLine("| minutes | ops/s | cum ops | docker | ws | committed | heap | frag | cache+k | native | gc_slack | live | gen2 | pause s | alloc MB/s |");
    Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

    for (int b = 0; b < bucketCount; b++)
    {
        double from = b * bucketSeconds / 60.0;
        double to = Math.Min((b + 1) * bucketSeconds, measureSeconds) / 60.0;
        double seconds = Math.Min(bucketSeconds, measureSeconds - b * bucketSeconds);
        Snapshot? last = LastInBucket(snaps, b);

        double? allocRate = alloc[b] is { } a && seconds > 0 ? a / 1024.0 / 1024.0 / seconds : null;
        double? opsRate = haveIntervals && seconds > 0 ? completedPerBucket[b] / seconds : null;

        Console.WriteLine(
            $"| {from:0}–{to:0} | {F0(opsRate)} | {FM(haveIntervals ? cumulativeOps[b] : null)} | {F0(last?.Docker)} | {F0(last?.Ws)} | {F0(last?.Committed)} | {F0(last?.Heap)} | {F0(last?.Frag)} | {F0(last?.CacheK)} | {F0(last?.Native)} | {F0(last?.GcSlack)} | {F0(last?.Live)} | {F0(gen2[b])} | {F1(pause[b])} | {F0(allocRate)} |");

        csv.Append(node).Append(',').Append(b).Append(',')
           .Append(from.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
           .Append(to.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
           .Append(C(opsRate)).Append(',').Append(C(haveIntervals ? cumulativeOps[b] : null)).Append(',')
           .Append(C(last?.Docker)).Append(',').Append(C(last?.Ws)).Append(',').Append(C(last?.Committed)).Append(',')
           .Append(C(last?.Heap)).Append(',').Append(C(last?.Frag)).Append(',')
           .Append(C(last?.CacheK)).Append(',').Append(C(last?.Native)).Append(',').Append(C(last?.GcSlack)).Append(',').Append(C(last?.Live)).Append(',')
           .Append(C(gen2[b])).Append(',').Append(C(pause[b])).Append(',').Append(C(allocRate)).AppendLine();
    }

    Console.WriteLine();

    if (haveIntervals)
    {
        Console.WriteLine("Growth per completed operation, first scrape to last scrape inside the window (KiB/op):");
        Console.WriteLine();
        Console.WriteLine("| component | first | last | Δ MiB | ops between | KiB/op |");
        Console.WriteLine("|---|---|---|---|---|---|");
        PrintSlope("docker", snaps, s => s.Docker);
        PrintSlope("ws", snaps, s => s.Ws);
        PrintSlope("committed", snaps, s => s.Committed);
        PrintSlope("heap", snaps, s => s.Heap);
        PrintSlope("cache+k", snaps, s => s.CacheK);
        PrintSlope("native", snaps, s => s.Native);
        PrintSlope("gc_slack", snaps, s => s.GcSlack);
        PrintSlope("live", snaps, s => s.Live);
        Console.WriteLine();
    }
}

Console.WriteLine("How to read it: the component whose KiB/op is closest to the docker figure owns the growth.");
Console.WriteLine("If cache+k owns it, the growth is outside the process and a gcdump cannot see it.");
Console.WriteLine("If live owns it, something managed accumulates and a loaded gcdump will name it.");
Console.WriteLine("If native owns it, look at RocksDB and the WAL, not at the GC.");
Console.WriteLine("If gc_slack owns it, the GC is holding committed regions; a heap hard limit or conserve-memory setting is the lever.");

if (emitCsv)
{
    Console.WriteLine();
    Console.WriteLine("```csv");
    Console.Write(csv.ToString());
    Console.WriteLine("```");
}

return 0;

// ---------------------------------------------------------------------------------------------

void PrintUsage()
{
    Console.Error.WriteLine("usage: memdecomp <runDir> [--bucket <seconds>] [--csv]");
    Console.Error.WriteLine("  <runDir>   a Caraxes scenario run directory, e.g. runs/scenarios/bank-soak-p1-w128-s1");
    Console.Error.WriteLine("  --bucket   bucket width in seconds inside the measured window (default 300)");
    Console.Error.WriteLine("  --csv      also print the table as CSV");
}

(DateTime Start, double Seconds) ReadWindow(string path)
{
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
    JsonElement root = doc.RootElement;
    string startText = root.GetProperty("measureStartUtc").GetString() ?? throw new InvalidDataException("measureStartUtc missing");
    DateTime start = DateTime.Parse(startText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    double seconds = root.GetProperty("measureSeconds").GetDouble();
    return (start, seconds);
}

int BucketOf(long unixMs)
{
    double offset = (unixMs - measureStartMs) / 1000.0;
    if (offset < 0 || offset >= measureSeconds)
        return -1;
    return (int)(offset / bucketSeconds);
}

// Operations completed between two instants, from the per-second intervals anchored at second 0.
double OpsBetween(long fromMs, long toMs)
{
    int from = Math.Clamp((int)((fromMs - measureStartMs) / 1000), 0, windowSeconds);
    int to = Math.Clamp((int)((toMs - measureStartMs) / 1000), 0, windowSeconds);
    double sum = 0;
    for (int s = from; s < to; s++)
        sum += completedPerSecond[s];
    return sum;
}

// One row per scrape instant inside the window. All node-metrics families sampled in one scrape
// share the instant, so the derived columns subtract values taken at the same moment; the docker
// figure is the nearest docker sample within tolerance, or null when none is close enough.
List<Snapshot> BuildSnapshots(List<MetricPoint> mine, List<(long Ms, double Mib, double LimitMib)>? docker)
{
    List<Snapshot> result = [];
    foreach (IGrouping<long, MetricPoint> scrape in mine.GroupBy(p => p.UnixMs).OrderBy(g => g.Key))
    {
        if (BucketOf(scrape.Key) < 0)
            continue;

        double? ws = null, committed = null, heap = null, frag = null;
        foreach (MetricPoint p in scrape)
        {
            if (p.Metric == wsFamily) ws = MibOf(p.Value);
            else if (p.Metric == committedFamily) committed = MibOf(p.Value);
            else if (p.Metric == heapFamily) heap = (heap ?? 0) + MibOf(p.Value);
            else if (p.Metric == fragFamily) frag = (frag ?? 0) + MibOf(p.Value);
        }

        double? dockerMib = null;
        if (docker is { Count: > 0 })
        {
            (long Ms, double Mib, double LimitMib) nearest = docker[0];
            long best = long.MaxValue;
            foreach (var sample in docker)
            {
                long distance = Math.Abs(sample.Ms - scrape.Key);
                if (distance < best)
                {
                    best = distance;
                    nearest = sample;
                }
                if (sample.Ms > scrape.Key + DockerMatchToleranceMs)
                    break;
            }
            if (best <= DockerMatchToleranceMs)
                dockerMib = nearest.Mib;
        }

        result.Add(new Snapshot(scrape.Key, dockerMib, ws, committed, heap, frag));
    }
    return result;
}

Snapshot? LastInBucket(List<Snapshot> snaps, int bucket)
{
    Snapshot? last = null;
    foreach (Snapshot s in snaps)
        if (BucketOf(s.Ms) == bucket)
            last = s;
    return last;
}

void PrintSlope(string name, List<Snapshot> snaps, Func<Snapshot, double?> pick)
{
    Snapshot? first = null, last = null;
    foreach (Snapshot s in snaps)
    {
        if (pick(s) is null)
            continue;
        first ??= s;
        last = s;
    }
    if (first is null || last is null || last.Ms <= first.Ms)
    {
        Console.WriteLine($"| {name} | n/a | n/a | n/a | n/a | n/a |");
        return;
    }
    double a = pick(first)!.Value, z = pick(last)!.Value;
    double ops = OpsBetween(first.Ms, last.Ms);
    string perOp = ops > 0 ? ((z - a) * 1024.0 / ops).ToString("0.000", CultureInfo.InvariantCulture) : "n/a";
    Console.WriteLine($"| {name} | {a:0} | {z:0} | {z - a:+0;-0;0} | {ops:N0} | {perOp} |");
}

// Counter increase inside each bucket as the sum of positive steps, so a node that restarted
// mid-window does not report a negative increase. Two label spellings are accepted because the
// generation label was renamed between runtime instrumentation versions.
double?[] CounterDeltaPerBucket(List<MetricPoint> mine, string? family, (string Key, string Value)? label, (string Key, string Value)? altLabel)
{
    double?[] result = new double?[bucketCount];
    if (family is null)
        return result;
    double? previous = null;
    foreach (MetricPoint p in mine.Where(p => p.Metric == family).OrderBy(p => p.UnixMs))
    {
        if (label is { } l && LabelValue(p.Labels, l.Key) != l.Value)
        {
            if (altLabel is not { } al || LabelValue(p.Labels, al.Key) != al.Value)
                continue;
        }
        int b = BucketOf(p.UnixMs);
        if (previous is { } prev && b >= 0)
        {
            double step = p.Value - prev;
            if (step > 0)
                result[b] = (result[b] ?? 0) + step;
            else
                result[b] ??= 0;
        }
        previous = p.Value;
    }
    return result;
}

static string? Resolve(HashSet<string> families, params string[] stems)
{
    foreach (string stem in stems)
    {
        // Prefer the exact stem, then a unit-suffixed spelling, and refuse a longer metric that merely
        // shares the prefix (heap_size must not resolve to a heap_size_fragmentation spelling).
        string? hit = families
            .Where(f => f == stem || (f.StartsWith(stem + "_", StringComparison.Ordinal) && !f.Substring(stem.Length + 1).Contains("fragmentation", StringComparison.Ordinal)))
            .OrderBy(f => f.Length)
            .FirstOrDefault();
        if (hit is not null)
            return hit;
    }
    return null;
}

static double MibOf(double bytes) => bytes / 1024.0 / 1024.0;
static string F0(double? v) => v is { } x ? x.ToString("0", CultureInfo.InvariantCulture) : "n/a";
static string F1(double? v) => v is { } x ? x.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
static string FM(double? v) => v is { } x ? (x / 1_000_000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M" : "n/a";
static string C(double? v) => v is { } x ? x.ToString("0.###", CultureInfo.InvariantCulture) : "";

// Exact label lookup on the canonical "k=v;k=v" form node-metrics.csv uses. Substring matching
// would fold partition_id=1 into partition_id=10, which is the trap the collector documents.
static string? LabelValue(string labels, string key)
{
    if (labels.Length == 0)
        return null;
    foreach (string pair in labels.Split(';'))
    {
        int eq = pair.IndexOf('=');
        if (eq > 0 && pair.AsSpan(0, eq).SequenceEqual(key))
            return pair[(eq + 1)..];
    }
    return null;
}

static List<MetricPoint> ReadNodeMetrics(string path)
{
    List<MetricPoint> list = [];
    using StreamReader reader = new(path);
    string? line = reader.ReadLine(); // header
    while ((line = reader.ReadLine()) is not null)
    {
        if (line.Length == 0)
            continue;
        List<string> fields = SplitCsv(line);
        if (fields.Count != 5)
            continue;
        if (!long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
            continue;
        if (!double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            continue;
        list.Add(new MetricPoint(ms, fields[1], fields[2], fields[3], value));
    }
    return list;
}

static IEnumerable<(DateTime Ts, string Container, double Mib, double LimitMib)> ReadMemorySamples(string path)
{
    foreach (string line in File.ReadLines(path).Skip(1))
    {
        string[] f = line.Split(',');
        if (f.Length < 4)
            continue;
        if (!DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime ts))
            continue;
        if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double mib))
            continue;
        double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double limit);
        yield return (ts.ToUniversalTime(), f[1], mib, limit);
    }
}

static IEnumerable<(int Second, long Completed)> ReadIntervals(string path)
{
    int secondIdx = -1, completedIdx = -1;
    bool headerSeen = false;
    foreach (string line in File.ReadLines(path))
    {
        string[] f = line.Split(',');
        if (!headerSeen)
        {
            headerSeen = true;
            secondIdx = Array.IndexOf(f, "second");
            completedIdx = Array.IndexOf(f, "completed");
            if (secondIdx < 0 || completedIdx < 0)
                yield break;
            continue;
        }
        if (f.Length <= Math.Max(secondIdx, completedIdx))
            continue;
        if (int.TryParse(f[secondIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) &&
            long.TryParse(f[completedIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out long c))
            yield return (s, c);
    }
}

// Minimal RFC 4180 field splitter: the labels column may carry a quoted comma or a doubled quote.
static List<string> SplitCsv(string line)
{
    List<string> fields = [];
    StringBuilder sb = new();
    bool quoted = false;
    for (int i = 0; i < line.Length; i++)
    {
        char ch = line[i];
        if (quoted)
        {
            if (ch == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else
            {
                sb.Append(ch);
            }
        }
        else if (ch == '"')
        {
            quoted = true;
        }
        else if (ch == ',')
        {
            fields.Add(sb.ToString());
            sb.Clear();
        }
        else
        {
            sb.Append(ch);
        }
    }
    fields.Add(sb.ToString());
    return fields;
}

readonly record struct MetricPoint(long UnixMs, string Node, string Metric, string Labels, double Value);

/// <summary>
/// One node's memory picture at one scrape instant, in MiB. The derived properties are null when
/// either operand is missing, so a column never silently reads as zero.
/// </summary>
sealed record Snapshot(long Ms, double? Docker, double? Ws, double? Committed, double? Heap, double? Frag)
{
    public double? CacheK => Docker is { } d && Ws is { } w ? d - w : null;
    public double? Native => Ws is { } w && Committed is { } c ? w - c : null;
    public double? GcSlack => Committed is { } c && Heap is { } h ? c - h : null;
    public double? Live => Heap is { } h && Frag is { } f ? h - f : null;
}
