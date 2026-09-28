// Attribute an EventPipe sampled-thread-time trace of a CamusDB node by thread role, thread state and component.
// usage: traceattr <file.nettrace> [--frames N] [--role <role>] [--grep <substring>] [--state running|lock|wait|gcpoll]
//                  [--callers <substring>]   (who calls frames matching substring, one level up, in the selected samples)
//        traceattr <file.nettrace> --components [--cpu-ms-per-op X]   (running samples by component and request kind)
//        traceattr <file.nettrace> --alloc [--kb-per-op X]            (gc-verbose trace: AllocationTick bytes by component, kind, site)
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

string path = args[0];
int topFrames = 40;
string? roleFilter = null, grep = null, callers = null;
string stateFilter = "running";
bool components = false, alloc = false; double? cpuMsPerOp = null, kbPerOp = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--frames") topFrames = int.Parse(args[++i]);
    else if (args[i] == "--role") roleFilter = args[++i];
    else if (args[i] == "--grep") grep = args[++i];
    else if (args[i] == "--state") stateFilter = args[++i];
    else if (args[i] == "--callers") callers = args[++i];
    else if (args[i] == "--components") components = true;
    else if (args[i] == "--alloc") alloc = true;
    else if (args[i] == "--cpu-ms-per-op") cpuMsPerOp = double.Parse(args[++i]);
    else if (args[i] == "--kb-per-op") kbPerOp = double.Parse(args[++i]);
}

string etlx = Path.ChangeExtension(path, ".etlx");
if (!File.Exists(etlx)) TraceLog.CreateFromEventPipeDataFile(path, etlx);
using TraceLog log = new(etlx);
if (alloc) { Attribution.Alloc(log, path, kbPerOp, topFrames > 0 ? Math.Min(topFrames, 25) : 10); return; }
TraceLogEventSource source = log.Events.GetSource();

var threadSamples = new Dictionary<int, List<string[]>>(); // tid -> stacks (leaf first)
var clr = source.Clr;
double gcSuspendMs = 0, otherSuspendMs = 0; int gcCount = 0; var gcByGen = new int[3];
double? suspendStart = null; bool suspendForGc = false; int otherSuspends = 0;
var gcStart = new Dictionary<int, double>(); double gcDurMs = 0;
clr.GCSuspendEEStart += d => { suspendStart = d.TimeStampRelativeMSec; suspendForGc = d.Reason is GCSuspendEEReason.SuspendForGC or GCSuspendEEReason.SuspendForGCPrep; };
clr.GCRestartEEStop += d =>
{
    if (suspendStart is double s)
    {
        if (suspendForGc) gcSuspendMs += d.TimeStampRelativeMSec - s; else { otherSuspendMs += d.TimeStampRelativeMSec - s; otherSuspends++; }
        suspendStart = null;
    }
};
clr.GCStart += d => { gcCount++; if (d.Depth < 3) gcByGen[d.Depth]++; gcStart[d.Count] = d.TimeStampRelativeMSec; };
clr.GCStop += d => { if (gcStart.Remove(d.Count, out double s)) gcDurMs += d.TimeStampRelativeMSec - s; };
long exceptions = 0; var exTypes = new Dictionary<string, int>();
clr.ExceptionStart += d =>
{
    exceptions++;
    string msg = d.ExceptionMessage.Length > 90 ? d.ExceptionMessage[..90] : d.ExceptionMessage;
    string k = d.ExceptionType + ": " + msg;
    exTypes[k] = exTypes.GetValueOrDefault(k) + 1;
};
double first = double.MaxValue, last = 0;
source.AllEvents += d =>
{
    if (d.ProviderName != "Microsoft-DotNETCore-SampleProfiler") return;
    first = Math.Min(first, d.TimeStampRelativeMSec); last = Math.Max(last, d.TimeStampRelativeMSec);
    TraceCallStack? cs = d.CallStack();
    var frames = new List<string>();
    for (; cs != null; cs = cs.Caller)
    {
        var m = cs.CodeAddress.Method;
        frames.Add(m != null ? m.FullMethodName : (cs.CodeAddress.ModuleName is { Length: > 0 } mn ? mn + "!?" : "?"));
    }
    if (!threadSamples.TryGetValue(d.ThreadID, out var list)) threadSamples[d.ThreadID] = list = new();
    list.Add(frames.ToArray());
};
source.Process();

double seconds = (last - first) / 1000.0;
long total = threadSamples.Values.Sum(l => l.Count);
double perThreadRate = threadSamples.Values.Max(l => l.Count) / seconds;
Console.WriteLine($"trace {Path.GetFileName(path)}: {seconds:F1} s, {threadSamples.Count} threads, {total} samples, ~{perThreadRate:F0} samples/s per thread");
Console.WriteLine($"GC: {gcCount} collections (gen0 {gcByGen[0]}, gen1 {gcByGen[1]}, gen2 {gcByGen[2]}), GC suspension {gcSuspendMs:F0} ms ({gcSuspendMs / seconds / 10:F2}% of wall), " +
                  $"GCStart->Stop {gcDurMs:F0} ms; non-GC EE suspensions {otherSuspends} totalling {otherSuspendMs:F0} ms ({otherSuspendMs / seconds / 10:F2}% of wall)");
Console.WriteLine($"exceptions: {exceptions} ({exceptions / seconds:F0}/s)");
foreach (var kv in exTypes.OrderByDescending(k => k.Value).Take(8)) Console.WriteLine($"  x{kv.Value,-8} {kv.Key}");

string Role(string[] st)
{
    string joined = string.Join("|", st.Reverse().Take(14));
    if (joined.Contains("RaftExecutorPool") || joined.Contains("RaftPartitionExecutor")) return "raft-executor";
    if (joined.Contains("FairWalScheduler")) return "wal-scheduler";
    if (joined.Contains("FairReadScheduler")) return "read-scheduler";
    if (joined.Contains("SocketAsyncEngine")) return "socket-engine";
    if (joined.Contains("ConsoleLoggerProcessor")) return "console-logger";
    if (joined.Contains("TimerQueue") || joined.Contains("TimerThread")) return "timer";
    if (joined.Contains("PortableThreadPool+GateThread")) return "tp-gate";
    if (joined.Contains("PortableThreadPool+WaitThread")) return "tp-wait";
    if (joined.Contains("WorkerThread.WorkerThreadStart") || joined.Contains("ThreadPoolWorkQueue.Dispatch")) return "threadpool";
    if (st.Length == 0) return "empty";
    return "other:" + st[^1].Split('(')[0];
}

// running | lock (blocked on a contended monitor/lock) | gcpoll (trapped at a GC/suspension poll) | wait (parked)
static string State(string[] st)
{
    if (st.Length == 0) return "wait";
    string leaf = st[0];
    if (leaf.Contains("PollGC")) return "gcpoll";
    int n = Math.Min(st.Length, 6);
    for (int i = 0; i < n; i++)
    {
        string f = st[i];
        if (f.Contains("Monitor.Enter_Slowpath") || f.Contains("Monitor.ReliableEnter") || f.Contains("Lock.EnterAndGetCurrentThreadId") ||
            f.Contains("Lock.TryEnterSlow") || f.Contains("Lock.Enter(") && i > 0 || f.Contains("SpinLock.ContinueTryEnter") ||
            f.Contains("ReaderWriterLockSlim.WaitOnEvent") || f.Contains("ReaderWriterLockSlim.TryEnter"))
            return "lock";
        if (f.Contains("LowLevelLifoSemaphore") || f.Contains("LowLevelMonitor") || f.Contains("Monitor.Wait") ||
            f.Contains("WaitHandle.Wait") || f.Contains("WaitSubsystem") || f.Contains("ManualResetEventSlim.Wait") ||
            f.Contains("SemaphoreSlim.WaitCore") || f.Contains("SemaphoreSlim.Wait(") || f.Contains("Thread.Sleep") ||
            f.Contains("WaitForSocketEvents") || f.Contains("WaitOneNoCheck") || f.Contains("Condition.Wait") ||
            f.Contains("ConsoleLoggerProcessor.TryDequeue") || f.Contains("BlockingCollection") ||
            f.Contains("PortableThreadPool+WorkerThread.WorkerThreadStart") && i == 0 ||
            f.Contains("SocketAsyncEngine.EventLoop") && i <= 1)
            return "wait";
    }
    return "running";
}

static string Library(string[] st)
{
    foreach (string f in st)
    {
        if (f.StartsWith("RocksDbSharp")) return "rocksdb";
        if (f.StartsWith("Google.Protobuf")) return "protobuf";
        if (f.StartsWith("System.Net.Security") || f.Contains("Interop+Ssl") || f.Contains("Interop+Crypto")) return "tls";
        if (f.StartsWith("Microsoft.AspNetCore.Server.Kestrel") || f.StartsWith("System.Net.Http")) return "http2/kestrel";
        if (f.StartsWith("Grpc.")) return "grpc";
        if (f.StartsWith("System.Net.Sockets")) return "sockets";
        if (f.StartsWith("Microsoft.Extensions.Logging")) return "logging";
        if (f.StartsWith("System.Diagnostics.Metrics") || f.StartsWith("OpenTelemetry") || f.StartsWith("Prometheus")) return "metrics";
        if (f.StartsWith("System.Runtime.EH") || f.Contains("Exception")) return "exceptions";
        if (f.StartsWith("Kommander")) return "kommander";
        if (f.StartsWith("Kahuna")) return "kahuna";
        if (f.StartsWith("CamusDB")) return "camusdb";
        if (f.StartsWith("Nixie")) return "nixie";
    }
    return st.Length > 0 && st[0].EndsWith("!?") || st.Length > 0 && st[0] == "?" ? "native-unresolved" : "runtime/bcl";
}

static string Owner(string[] st)
{
    foreach (string f in st)
    {
        if (f.StartsWith("Kommander.WAL")) return "Kommander.WAL";
        if (f.StartsWith("Kommander.Scheduling")) return "Kommander.Scheduling";
        if (f.StartsWith("Kommander.Communication")) return "Kommander.Communication";
        if (f.StartsWith("Kommander")) return "Kommander(core)";
        if (f.StartsWith("Kahuna")) return "Kahuna";
        if (f.StartsWith("CamusDB")) return "CamusDB";
        if (f.StartsWith("Nixie")) return "Nixie";
        if (f.StartsWith("Grpc.")) return "Grpc";
        if (f.StartsWith("Microsoft.AspNetCore")) return "AspNetCore";
    }
    return "none";
}

var roleState = new Dictionary<(string role, string state), long>();
var roleThreads = new Dictionary<string, int>();
var byRoleLib = new Dictionary<(string, string), long>();
var byRoleOwner = new Dictionary<(string, string), long>();
var inclusive = new Dictionary<string, long>();
var leafSel = new Dictionary<string, long>();
var callerCounts = new Dictionary<string, long>();
long selected = 0;
foreach (var (tid, stacks) in threadSamples)
{
    string role = stacks.GroupBy(Role).OrderByDescending(g => g.Count()).First().Key;
    roleThreads[role] = roleThreads.GetValueOrDefault(role) + 1;
    foreach (var st in stacks)
    {
        string state = State(st);
        roleState[(role, state)] = roleState.GetValueOrDefault((role, state)) + 1;
        if (stateFilter != "all" && state != stateFilter) continue;
        if (roleFilter != null && role != roleFilter) continue;
        if (grep != null && !st.Any(f => f.Contains(grep))) continue;
        selected++;
        var lk = (role, Library(st)); byRoleLib[lk] = byRoleLib.GetValueOrDefault(lk) + 1;
        var ok = (role, Owner(st)); byRoleOwner[ok] = byRoleOwner.GetValueOrDefault(ok) + 1;
        foreach (string f in st.Distinct()) inclusive[f] = inclusive.GetValueOrDefault(f) + 1;
        string leaf = st.Length > 0 ? st[0] : "?";
        leafSel[leaf] = leafSel.GetValueOrDefault(leaf) + 1;
        if (callers != null)
        {
            // Nearest application frame above the first frame matching `callers`.
            int idx = Array.FindIndex(st, f => f.Contains(callers));
            if (idx >= 0)
            {
                string c = st.Skip(idx + 1).FirstOrDefault(f => f.StartsWith("Kommander") || f.StartsWith("Kahuna") || f.StartsWith("CamusDB") || f.StartsWith("Nixie") || f.StartsWith("Grpc")) ?? "none";
                callerCounts[c] = callerCounts.GetValueOrDefault(c) + 1;
            }
        }
    }
}

if (components) { Attribution.Cpu(threadSamples, State, cpuMsPerOp, topFrames); return; }

double Thr(long samples) => samples / perThreadRate / seconds; // average threads in that state
Console.WriteLine();
Console.WriteLine("role (avg threads)           threads  running     lock   gcpoll     wait");
foreach (var role in roleThreads.Keys.OrderByDescending(r => roleState.GetValueOrDefault((r, "running")) + roleState.GetValueOrDefault((r, "lock"))))
    Console.WriteLine($"{role,-30}{roleThreads[role],6}{Thr(roleState.GetValueOrDefault((role, "running"))),9:F2}{Thr(roleState.GetValueOrDefault((role, "lock"))),9:F2}{Thr(roleState.GetValueOrDefault((role, "gcpoll"))),9:F2}{Thr(roleState.GetValueOrDefault((role, "wait"))),9:F2}");

string sel = $"state {stateFilter}{(roleFilter != null ? ", role " + roleFilter : "")}{(grep != null ? ", stacks containing " + grep : "")}";
void Table(string title, Dictionary<(string, string), long> d)
{
    Console.WriteLine();
    Console.WriteLine($"{title} [{sel}: {selected} samples = {Thr(selected):F2} threads]");
    foreach (var g in d.GroupBy(k => k.Key.Item1).OrderByDescending(g => g.Sum(x => x.Value)))
    {
        long gs = g.Sum(x => x.Value);
        if (gs * 200 < selected) continue;
        Console.WriteLine($"  {g.Key}: {100.0 * gs / selected:F1}%  ({Thr(gs):F2} thr)");
        foreach (var kv in g.OrderByDescending(x => x.Value).Where(x => x.Value * 200 >= selected))
            Console.WriteLine($"      {kv.Key.Item2,-24}{100.0 * kv.Value / selected,6:F1}%  {Thr(kv.Value),6:F2} thr");
    }
}
Table("library (leaf-first)", byRoleLib);
Table("owner (innermost app namespace)", byRoleOwner);

Console.WriteLine();
Console.WriteLine($"top inclusive frames [{sel}]:");
foreach (var kv in inclusive.OrderByDescending(k => k.Value)
             .Where(k => !k.Key.StartsWith("System.Threading") && !k.Key.StartsWith("System.Runtime.CompilerServices") || grep != null).Take(topFrames))
    Console.WriteLine($"  {100.0 * kv.Value / selected,5:F1}%  {Thr(kv.Value),5:F2} thr  {Trunc(kv.Key)}");
Console.WriteLine();
Console.WriteLine($"top leaf frames [{sel}]:");
foreach (var kv in leafSel.OrderByDescending(k => k.Value).Take(topFrames / 2))
    Console.WriteLine($"  {100.0 * kv.Value / selected,5:F1}%  {Thr(kv.Value),5:F2} thr  {Trunc(kv.Key)}");
if (callers != null)
{
    Console.WriteLine();
    Console.WriteLine($"nearest app caller of '{callers}':");
    foreach (var kv in callerCounts.OrderByDescending(k => k.Value).Take(topFrames / 2))
        Console.WriteLine($"  {100.0 * kv.Value / selected,5:F1}%  {Thr(kv.Value),5:F2} thr  {Trunc(kv.Key)}");
}

static string Trunc(string s) => s.Length > 160 ? s[..160] : s;
