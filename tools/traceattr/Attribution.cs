// Component attribution for CamusDB feature ffbda1b5 (Phase 4) task 6ba448df: CPU samples (sampled-thread-time,
// running state only) or allocation bytes (gc-verbose AllocationTick) split by component, by request kind and, for
// allocations, by site. Shares only; turn them into per-op figures with the unperturbed unit costs of the same run
// (`--cpu-ms-per-op` / `--kb-per-op`, from `p3c regime` unit-costs.json).
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

static class Attribution
{
    // Innermost owner, walking from the leaf: the first frame that names a component decides. A stack inside the
    // exception machinery is charged to "exceptions" whoever threw.
    public static string Component(string[] st)
    {
        foreach (string f in st)
            if (f.StartsWith("System.Runtime.EH") || f.StartsWith("System.Exception..ctor") || f.Contains("Exception.CaptureDispatchState")
                || f.StartsWith("System.Runtime.ExceptionServices")) return "exceptions";
        foreach (string f in st)
        {
            if (f.StartsWith("RocksDbSharp")) return "rocksdb";
            if (f.StartsWith("Google.Protobuf")) return "grpc+protobuf";
            if (f.StartsWith("Grpc.")) return "grpc+protobuf";
            if (f.StartsWith("Microsoft.AspNetCore") || f.StartsWith("System.Net.Http")) return "kestrel/http2";
            if (f.StartsWith("System.Net.Security") || f.Contains("Interop+Ssl") || f.Contains("Interop+Crypto")) return "tls";
            if (f.StartsWith("System.Net.Sockets")) return "sockets";
            if (f.StartsWith("Microsoft.Extensions.Logging")) return "logging";
            if (f.StartsWith("System.Diagnostics.Metrics") || f.StartsWith("OpenTelemetry") || f.StartsWith("Prometheus")) return "metrics";
            if (f.StartsWith("Kommander.WAL")) return "kommander.wal";
            if (f.StartsWith("Kommander")) return "kommander";
            if (f.StartsWith("Kahuna")) return "kahuna";
            if (f.StartsWith("Nixie")) return "nixie(actors)";
            if (Camus(f) is string c) return c;
        }
        return "runtime/bcl";
    }

    static readonly string[] BindPlan =
    [
        "QueryPlanner", "JoinQueryPlanner", "PlanCache", "IndexScanSelector", "CostEstimator", "CardinalityEstimator",
        "PredicateAnalyzer", "QueryBinder", "SelectBindPipeline", "RequiredColumnAnalyzer", "ProjectionPushdownPlanner",
        "IndexScanBoundAnalysis", "QueryShapeComputer", "QueryExpressionClassifier", "ViewExpander", "SubqueryRewriter",
        "PlaceholderCollector", "StatementRoutingCollector", "StatementRoutingResolver",
    ];
    static readonly string[] RowCodec =
    [
        "CompiledRowCodec", "RowEncoder", "RowView", "BranchKvCodec", "KeyEncoder", "KvKeyBuilder", "IndexIncludeValueCodec",
        "RowSlotAdapter", "RowStorageForms", "LargeValue", "CompactRowEncoder", "CompactRowJsonWriter", "ColumnValueWireCodec",
    ];

    static string? Camus(string f)
    {
        if (f.StartsWith("QUT.") || f.StartsWith("CamusDB.Core.SQLParser")) return "camus.sql-parse";
        if (!f.StartsWith("CamusDB")) return null;
        // Images before the Commands/ folder move name these CamusDB.Core.CommandsExecutor / CommandsValidator.
        f = f.Replace("CamusDB.Core.CommandsExecutor", "CamusDB.Core.Commands.Executor").Replace("CamusDB.Core.CommandsValidator", "CamusDB.Core.Commands.Validator");
        string type = TypeOf(f);
        if (f.StartsWith("CamusDB.Core.Serializer") || RowCodec.Any(r => type.StartsWith(r))) return "camus.row-codec";
        if (f.StartsWith("CamusDB.Core.Commands.Validator") || BindPlan.Any(b => type.StartsWith(b))) return "camus.bind-plan";
        if (f.StartsWith("CamusDB.Core.Transactions") || type.StartsWith("TransactionStarter") || type.StartsWith("SerializableRetryHelper"))
            return "camus.txn-coordinator";
        if (f.StartsWith("CamusDB.Core.Storage")) return "camus.kv-store";
        if (f.StartsWith("CamusDB.Core.Commands.Executor.Controllers.Queries") || f.StartsWith("CamusDB.Core.Commands.Executor.Models.Queries")
            || type.StartsWith("QueryExecutor") || type.StartsWith("SqlExecutor")) return "camus.query-exec";
        if (f.StartsWith("CamusDB.Core.Commands.Executor.Controllers.DML") || f.StartsWith("CamusDB.Core.Flux")
            || type.StartsWith("RowUpdater") || type.StartsWith("RowInserter") || type.StartsWith("RowDeleter")
            || type.StartsWith("MutationRowRecheck") || type.StartsWith("CheckEnforcer") || type.StartsWith("CheckEvaluator")) return "camus.dml-exec";
        if (type.StartsWith("CommandExecutor") || type.StartsWith("ExecutorContext")) return "camus.dispatch";
        if (f.StartsWith("CamusDB.Core.Cache")) return "camus.query-cache";
        if (f.StartsWith("CamusDB.Core.Catalogs") || type.StartsWith("DatabaseRegistry") || type.StartsWith("DatabaseOpener")
            || type.StartsWith("TableOpener")) return "camus.catalog";
        if (f.StartsWith("CamusDB.App") || f.StartsWith("CamusDB.Grpc")) return "camus.grpc-service";
        return "camus.other";
    }

    // "Ns.Type+Nested`1[...].Method(...)" -> "Type+Nested"
    static string TypeOf(string frame)
    {
        string head = frame.Split('(')[0];
        int lastDot = head.LastIndexOf('.');
        if (lastDot <= 0) return head;
        string typeFull = head[..lastDot];
        return typeFull[(typeFull.LastIndexOf('.') + 1)..];
    }

    // Request kind by any frame of the stack; async continuations lose their gRPC entry frame, so the markers are the
    // statement executors as well as the service methods. Order matters: a commit runs inside no statement.
    public static string Kind(string[] st)
    {
        string s = string.Join("|", st);
        if (s.Contains("CommitTransaction") || s.Contains("KvTransaction.Commit") || s.Contains("KvTransactionsManager.Commit")) return "commit";
        if (s.Contains("RollbackTransaction") || s.Contains("KvTransaction.Rollback") || s.Contains("KvTransactionsManager.Rollback")) return "rollback";
        if (s.Contains("ExecuteNonQuery") || s.Contains("NonQueryStatementDispatcher") || s.Contains("RowUpdater") || s.Contains("SQLExecutorUpdateCreator")
            || s.Contains("RowInserter") || s.Contains("RowDeleter")) return "non_query";
        if (s.Contains("ExecuteQuery") || s.Contains("SelectStatementExecutor") || s.Contains("QueryExecutor") || s.Contains("QueryScanner")) return "query";
        if (s.Contains("StartTransaction") || s.Contains("TransactionStarter")) return "begin";
        if (s.Contains("BatchExecute")) return "batch-frame(other)";
        if (s.Contains("CamusDB")) return "camus(no kind marker)";
        return "no CamusDB frame";
    }

    // "Ns.Type+<Method>d__16.MoveNext" -> "Ns.Type" (async state machines folded into their declaring type)
    static string OwnerType(string site)
    {
        int dot = site.LastIndexOf('.');
        string t = dot > 0 ? site[..dot] : site;
        int plus = t.IndexOf('+');
        return plus > 0 ? t[..plus] : t;
    }

    // Cross-cutting slices for the features that own them. Inclusive (a stack can be in several); by any frame.
    static readonly string[] PointRead =
    [
        "QueryUsingUniqueIndex", "KvIndexAccessor", "KvBranchReader", "KvRowAccessor", "TryGetHandler", "TryExistsHandler",
        "LocateAndTryGetValue", "LocateAndTryExistsValue", "LocateAndTryGetManyValues", "RegisterAndTryReadValue", "LocalKeyValueReadOperations",
    ];
    public static IEnumerable<string> Slices(string[] st, string comp)
    {
        if (st.Any(f => PointRead.Any(p => f.Contains(p)))) yield return "point-read path, PK index hop + row hop (97b32164)";
        bool app = st.Any(f => f.StartsWith("Kommander") || f.StartsWith("Kahuna") || f.StartsWith("Nixie"));
        bool serverTransport = st.Any(f => f.StartsWith("Microsoft.AspNetCore.Server.Kestrel") || f.StartsWith("System.Net.Security"))
                               && !st.Any(f => f.StartsWith("Grpc.Net.Client"));
        if (comp is "camus.grpc-service" or "camus.bind-plan" or "camus.catalog" or "camus.dispatch" or "camus.sql-parse"
            || serverTransport && !app || st.Any(f => f.StartsWith("CamusDB.App.Grpc")) && comp is "grpc+protobuf" or "kestrel/http2" or "tls" or "sockets")
            yield return "per-request exchange: server transport + service + parse/bind/plan/route + catalog (be9c297a; upper bound)";
        if (st.Any(f => f.StartsWith("Grpc.Net.Client"))) yield return "outbound inter-node gRPC client (Raft/Kahuna forwards)";
    }

    // Innermost application frame: the site an allocation is charged to.
    public static string Site(string[] st)
    {
        foreach (string f in st)
            if (f.StartsWith("CamusDB") || f.StartsWith("Kahuna") || f.StartsWith("Kommander") || f.StartsWith("Nixie")
                || f.StartsWith("Grpc.") || f.StartsWith("Google.Protobuf") || f.StartsWith("Microsoft.AspNetCore") || f.StartsWith("QUT."))
                return f.Split('(')[0];
        return st.Length > 0 ? st[0].Split('(')[0] + " (no app frame)" : "?";
    }

    static string[] Frames(TraceCallStack? cs)
    {
        var frames = new List<string>();
        for (; cs != null; cs = cs.Caller)
        {
            var m = cs.CodeAddress.Method;
            frames.Add(m != null ? m.FullMethodName : (cs.CodeAddress.ModuleName is { Length: > 0 } mn ? mn + "!?" : "?"));
        }
        return [.. frames];
    }

    // Allocation mode: AllocationTick bytes by component, kind, site and type.
    public static void Alloc(TraceLog log, string path, double? kbPerOp, int top)
    {
        TraceLogEventSource source = log.Events.GetSource();
        var byComp = new Dictionary<string, double>();
        var byKind = new Dictionary<string, double>();
        var byCompKind = new Dictionary<(string, string), double>();
        var bySite = new Dictionary<(string site, string type), double>();
        var byType = new Dictionary<string, double>();
        var byOwnerType = new Dictionary<string, double>();
        var byFrame = new Dictionary<string, double>();
        var bySlice = new Dictionary<string, double>();
        double total = 0, first = double.MaxValue, last = 0; long ticks = 0, noStack = 0;
        source.Clr.GCAllocationTick += d =>
        {
            double bytes = d.AllocationAmount64;
            first = Math.Min(first, d.TimeStampRelativeMSec); last = Math.Max(last, d.TimeStampRelativeMSec);
            string[] st = Frames(d.CallStack());
            if (st.Length == 0) noStack++;
            ticks++; total += bytes;
            string comp = Component(st), kind = Kind(st), type = d.TypeName ?? "?";
            byComp[comp] = byComp.GetValueOrDefault(comp) + bytes;
            byKind[kind] = byKind.GetValueOrDefault(kind) + bytes;
            byCompKind[(comp, kind)] = byCompKind.GetValueOrDefault((comp, kind)) + bytes;
            bySite[(Site(st), type)] = bySite.GetValueOrDefault((Site(st), type)) + bytes;
            byType[type] = byType.GetValueOrDefault(type) + bytes;
            string site = Site(st);
            byFrame[site] = byFrame.GetValueOrDefault(site) + bytes;
            string owner = comp + "  " + OwnerType(site);
            byOwnerType[owner] = byOwnerType.GetValueOrDefault(owner) + bytes;
            foreach (string sl in Slices(st, comp))
            {
                bySlice[sl] = bySlice.GetValueOrDefault(sl) + bytes;
                bySlice[$"  {sl[..Math.Min(sl.Length, 40)]} / {kind}"] = bySlice.GetValueOrDefault($"  {sl[..Math.Min(sl.Length, 40)]} / {kind}") + bytes;
            }
        };
        source.Process();
        double seconds = (last - first) / 1000;
        Console.WriteLine($"alloc {Path.GetFileName(path)}: {seconds:F1} s, {ticks} AllocationTick events ({noStack} without a stack), " +
                          $"{total / 1e6 / seconds:F0} MB/s sampled{(kbPerOp is double k ? $"; scaled to {k:F1} KB/op" : "")}");
        string Per(double b) => kbPerOp is double k ? $"{k * b / total,8:F2} KB/op" : "";
        void Print(string title, IEnumerable<KeyValuePair<string, double>> rows)
        {
            Console.WriteLine(); Console.WriteLine(title);
            foreach (var kv in rows.OrderByDescending(r => r.Value))
                if (kv.Value * 500 >= total) Console.WriteLine($"  {100 * kv.Value / total,5:F1}%  {Per(kv.Value)}  {kv.Key}");
        }
        Print("by component (innermost owner):", byComp);
        Print("by request kind:", byKind);
        Print("slices (inclusive, overlapping):", bySlice);
        Print("component x kind:", byCompKind.Select(kv => KeyValuePair.Create($"{kv.Key.Item1,-24} {kv.Key.Item2}", kv.Value)));
        Console.WriteLine(); Console.WriteLine($"top {top} allocation sites (innermost app frame, all types):");
        foreach (var kv in byFrame.OrderByDescending(r => r.Value).Take(top))
            Console.WriteLine($"  {100 * kv.Value / total,5:F1}%  {Per(kv.Value)}  {Trunc(kv.Key, 150)}");
        Console.WriteLine(); Console.WriteLine($"top {top} owner types (component, innermost app type):");
        foreach (var kv in byOwnerType.OrderByDescending(r => r.Value).Take(top))
            Console.WriteLine($"  {100 * kv.Value / total,5:F1}%  {Per(kv.Value)}  {Trunc(kv.Key, 150)}");
        Console.WriteLine(); Console.WriteLine($"top {top} allocation sites (innermost app frame, type):");
        foreach (var kv in bySite.OrderByDescending(r => r.Value).Take(top))
            Console.WriteLine($"  {100 * kv.Value / total,5:F1}%  {Per(kv.Value)}  {Trunc(kv.Key.site, 120)}  <{Trunc(kv.Key.type, 80)}>");
        Console.WriteLine(); Console.WriteLine($"top {top} types:");
        foreach (var kv in byType.OrderByDescending(r => r.Value).Take(top))
            Console.WriteLine($"  {100 * kv.Value / total,5:F1}%  {Per(kv.Value)}  {Trunc(kv.Key, 140)}");
    }

    // CPU mode: running samples by component and kind (thread roles and states are the default report's business).
    public static void Cpu(Dictionary<int, List<string[]>> threadSamples, Func<string[], string> state, double? msPerOp, int top)
    {
        var byComp = new Dictionary<string, long>();
        var byKind = new Dictionary<string, long>();
        var byCompKind = new Dictionary<(string, string), long>();
        var camusFrames = new Dictionary<string, long>();
        var byOwnerType = new Dictionary<string, long>();
        var bySlice = new Dictionary<string, long>();
        long total = 0;
        foreach (var st in threadSamples.Values.SelectMany(l => l))
        {
            if (state(st) != "running") continue;
            // The FileSystemWatcher thread blocked in read(2) shows as running in every trace (README); not CPU.
            if (st.Any(f => f.Contains("FileSystemWatcher") || f.Contains("INotify"))) continue;
            total++;
            string comp = Component(st), kind = Kind(st);
            byComp[comp] = byComp.GetValueOrDefault(comp) + 1;
            byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            byCompKind[(comp, kind)] = byCompKind.GetValueOrDefault((comp, kind)) + 1;
            string owner = comp + "  " + OwnerType(Site(st));
            byOwnerType[owner] = byOwnerType.GetValueOrDefault(owner) + 1;
            foreach (string sl in Slices(st, comp))
            {
                bySlice[sl] = bySlice.GetValueOrDefault(sl) + 1;
                bySlice[$"  {sl[..Math.Min(sl.Length, 40)]} / {kind}"] = bySlice.GetValueOrDefault($"  {sl[..Math.Min(sl.Length, 40)]} / {kind}") + 1;
            }
            foreach (string f in st.Where(f => f.StartsWith("CamusDB")).Select(f => f.Split('(')[0]).Distinct())
                camusFrames[f] = camusFrames.GetValueOrDefault(f) + 1;
        }
        Console.WriteLine($"cpu: {total} running samples{(msPerOp is double m ? $"; scaled to {m:F3} ms CPU/op" : "")}");
        string Per(long n) => msPerOp is double m ? $"{m * n / total * 1000,8:F1} us/op" : "";
        void Print(string title, IEnumerable<KeyValuePair<string, long>> rows)
        {
            Console.WriteLine(); Console.WriteLine(title);
            foreach (var kv in rows.OrderByDescending(r => r.Value))
                if (kv.Value * 500 >= total) Console.WriteLine($"  {100.0 * kv.Value / total,5:F1}%  {Per(kv.Value)}  {kv.Key}");
        }
        Print("by component (innermost owner):", byComp);
        Print("by request kind:", byKind);
        Print("slices (inclusive, overlapping):", bySlice);
        Print("component x kind:", byCompKind.Select(kv => KeyValuePair.Create($"{kv.Key.Item1,-24} {kv.Key.Item2}", kv.Value)));
        Console.WriteLine(); Console.WriteLine($"top {top} owner types (component, innermost app type):");
        foreach (var kv in byOwnerType.OrderByDescending(r => r.Value).Take(top))
            Console.WriteLine($"  {100.0 * kv.Value / total,5:F1}%  {Per(kv.Value)}  {Trunc(kv.Key, 150)}");
        Console.WriteLine(); Console.WriteLine($"top {top} inclusive CamusDB frames:");
        foreach (var kv in camusFrames.OrderByDescending(r => r.Value).Take(top))
            Console.WriteLine($"  {100.0 * kv.Value / total,5:F1}%  {Per(kv.Value)}  {Trunc(kv.Key, 150)}");
    }

    static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;
}
