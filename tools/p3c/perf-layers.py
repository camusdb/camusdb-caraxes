#!/usr/bin/env python3
"""Attribute a perf profile of a CamusDB node by component.

Input is the diagnostics directory perf-leader-arm.sh writes: perf.script.txt (perf script with
-F comm,tid,period,ip,sym,dso) and perf-1.map (the node's JIT perf map, DOTNET_PerfMapEnabled=1).
JIT frames perf left as [unknown] are resolved from the map. libcoreclr frames need the runtime's
debug file: fetch it by build id from https://msdl.microsoft.com/download/symbols/_.debug/elf-buildid-sym-<id>/_.debug,
place it at <symfs>/usr/lib/debug/.build-id/<id[0:2]>/<id[2:]>.debug beside a copy of the image's
/usr/share/dotnet, and run perf script again with --symfs <symfs>.

  perf-layers.py <dir>                          CPU by owning component and by what the leaf frame is
  perf-layers.py <dir> --types '^CamusDB' 6     owner split inside the matching assemblies, by type name depth
  perf-layers.py <dir> --incl 'RowUpdater'      inclusive share of each frame that matches
  perf-layers.py <dir> --any 'A;;B'             share of samples whose stack holds a frame matching each pattern
  perf-layers.py <dir> --callers 'Monitor_' 2   nearest managed callers of leaf-side frames that match
  perf-layers.py <dir> --allocs                 call sites that reach the allocator's slow path (follows bytes allocated)

"Owner" is the leaf-most frame of a named component (CamusDB, Kahuna, Kommander, Solarium, Nixie,
gRPC/Kestrel, protobuf, OpenTelemetry), so runtime, BCL and kernel time count for the component
that called them. Frames are normalised to assembly!Type::Method before matching, so a type named
in a method's signature never matches.
"""
import bisect
import collections
import os
import pickle
import re
import sys

GROUPS = [("CamusDB.Core", r"^CamusDB\.Core$"), ("CamusDB (server host)", r"^CamusDB$"), ("Kahuna.Core", r"^Kahuna"),
          ("Kommander", r"^Kommander"), ("Solarium", r"^Solarium"), ("Nixie", r"^Nixie"),
          ("gRPC/Kestrel/HTTP2", r"^Grpc|^Microsoft\.AspNetCore"), ("protobuf", r"^Google\.Protobuf"),
          ("OpenTelemetry", r"^OpenTelemetry"), ("LZ4", r"^K4os"), ("Extensions (logging/DI/options)", r"^Microsoft\.Extensions")]
GROUP_RX = [(n, re.compile(r)) for n, r in GROUPS]
DECL = re.compile(r"\[([^\]]+)\] (\S+)$")
TIER = re.compile(r"\[(Optimized|Tier0|Tier1|OSR|PreJIT|QuickJitted|Instrumented[^\]]*|Unoptimized|MinOpt)[^\]]*\]")
ALLOC_SLOW = re.compile(r"try_allocate_more_space|allocate_more_space|GCHeap::Alloc|RhpGcAlloc|adjust_limit_clr|allocate_uoh|a_fit_|soh_try_fit|gc_heap::allocate")


def load_raw(d):
    """Samples as (thread name, period, frames leaf to root), cached beside the input."""
    cache = os.path.join(d, "stacks.cache.pkl")
    if os.path.exists(cache):
        return pickle.load(open(cache, "rb"))
    rows = []
    with open(os.path.join(d, "perf-1.map"), errors="replace") as f:
        for line in f:
            p = line.rstrip("\n").split(" ", 2)
            if len(p) < 3:
                continue
            try:
                a = int(p[0], 16)
                rows.append((a, a + int(p[1], 16), p[2]))
            except ValueError:
                continue
    rows.sort()
    starts = [r[0] for r in rows]

    def jit(ip):
        i = bisect.bisect_right(starts, ip) - 1
        return rows[i][2] if i >= 0 and ip < rows[i][1] else None

    samples, cur = [], None
    with open(os.path.join(d, "perf.script.txt"), errors="replace") as f:
        for line in f:
            if not line.strip():
                if cur:
                    samples.append(cur)
                cur = None
                continue
            if not line.startswith(("\t", " ")):
                m = re.match(r"^(.*?)\s+(\d+)\s+(\d+)\s*$", line.rstrip())
                cur = (m.group(1).strip(), int(m.group(3)), []) if m else None
                continue
            if cur is None:
                continue
            m = re.match(r"^\s+([0-9a-f]+)\s+(.*)\s+\(([^()]*)\)$", line.rstrip().replace(" (deleted))", ")"))
            if not m:
                continue
            sym = m.group(2).strip()
            if sym == "[unknown]" or not sym:
                j = jit(int(m.group(1), 16))
                sym = TIER.sub("", j).strip() if j else "[" + os.path.basename(m.group(3).split(" ")[0]) + "]"
            cur[2].append(sym)
    if cur:
        samples.append(cur)
    try:
        pickle.dump(samples, open(cache, "wb"))
    except OSError:
        pass
    return samples


def norm(frame):
    i = frame.find("::")
    if i > 0 and not frame.startswith(("SVR::", "WKS::")) and "[" in frame[:i]:
        m = DECL.search(frame[:i])
        if m:
            return m.group(1) + "!" + m.group(2) + "::" + frame[i + 2:].split("(")[0]
    return "native!" + frame


def group_of(frame):
    if frame.startswith("native!"):
        return None
    a = frame.split("!", 1)[0]
    for n, r in GROUP_RX:
        if r.search(a):
            return n
    return None


def is_gc_thread(comm):
    return "Server GC" in comm or "BGC" in comm


def self_kind(frame, comm):
    if is_gc_thread(comm):
        return "GC threads"
    if frame.startswith("native!"):
        s = frame[7:]
        if s.startswith(("SVR::", "WKS::")) or "gc_heap" in s or "GCHeap" in s:
            return "GC work on application threads"
        if s.startswith("[[kernel") or ("::" not in s and "(" not in s and not s.startswith(("[", "stub ", "Rhp", "JIT_"))):
            return "kernel"
        if "Monitor" in s or "ObjHeader" in s or "AwareLock" in s or "SpinWait" in s:
            return "runtime: monitor locks and spin"
        if s.startswith(("RhpNew", "JIT_New")) or "Alloc" in s:
            return "runtime: allocation"
        if "WriteBarrier" in s or "Memmove" in s:
            return "runtime: write barriers and memmove"
        if s.startswith("stub "):
            return "runtime: stubs (interface and generic dispatch)"
        if s.startswith(("[lib", "[ld-", "[[vdso")):
            return "libc, ld, vdso"
        return "runtime: other native"
    g = group_of(frame)
    if g:
        return "managed self: " + g
    t = frame.split("!", 1)[1]
    if re.search(r"Threading\.Tasks|CompilerServices\.(Async|TaskAwaiter|ValueTaskAwaiter|ConfiguredValueTask|PoolingAsync)|ExecutionContext|ManualResetValueTaskSource", t):
        return "BCL: async and task machinery"
    if re.search(r"ThreadPool|Threading\.(Lock|Monitor|SpinWait|Volatile|Interlocked|Semaphore|Thread|LowLevel|ManualResetEvent|CancellationToken)", t):
        return "BCL: thread pool and synchronisation"
    if re.search(r"Collections|EqualityComparer|GetHashCode|Marvin", t):
        return "BCL: collections and hashing"
    if re.search(r"String|Text|Span|Buffer|Number|Utf8|Ascii|Memory|Buffers|Convert|DateTime|Guid", t):
        return "BCL: strings, spans, buffers"
    return "BCL: other"


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    d, args = sys.argv[1], sys.argv[2:]

    def opt(name, k=1):
        return args[args.index(name) + 1: args.index(name) + 1 + k] if name in args else None

    raw = load_raw(d)
    samples = [(c, p, [norm(f) for f in fr]) for c, p, fr in raw if fr]
    total = sum(p for _, p, _ in samples)
    share = lambda v: f"{100 * v / total:6.2f}%"

    def owner_of(comm, frames):
        if is_gc_thread(comm):
            return "GC threads"
        for f in frames:
            g = group_of(f)
            if g:
                return g
        return "runtime and BCL only (thread-pool dispatch, timers, finalizer)"

    if opt("--types"):
        pattern, depth = opt("--types", 2)
        rx, c = re.compile(pattern), collections.Counter()
        for comm, p, fr in samples:
            if is_gc_thread(comm):
                continue
            for f in fr:
                if group_of(f):
                    if rx.search(f.split("!", 1)[0]):
                        ty = re.sub(r"`\d+.*", "", re.sub(r"\+<.*", "", f.split("!", 1)[1].split("::")[0]))
                        c[".".join(ty.split(".")[:int(depth)])] += p
                    break
        for n, v in c.most_common(50):
            print(share(v), n)
    elif opt("--incl"):
        rx, c = re.compile(opt("--incl")[0]), collections.Counter()
        for comm, p, fr in samples:
            for f in set(fr):
                if rx.search(f):
                    c[f] += p
        for n, v in c.most_common(50):
            print(share(v), n[:170])
    elif opt("--any"):
        for pattern in opt("--any")[0].split(";;"):
            rx = re.compile(pattern)
            print(share(sum(p for _, p, fr in samples if any(rx.search(f) for f in fr))), pattern)
    elif opt("--callers"):
        a = opt("--callers", 2)
        rx, depth, c, n = re.compile(a[0]), int(a[1]) if len(a) > 1 and a[1].isdigit() else 1, collections.Counter(), 0
        for comm, p, fr in samples:
            hit = max((i for i, f in enumerate(fr[:8]) if rx.search(f)), default=-1)
            if hit < 0:
                continue
            n += p
            managed = [f.split("!", 1)[1] for f in fr[hit + 1:] if not f.startswith("native!")][:depth]
            c[" <- ".join(m[-95:] for m in managed)] += p
        print(share(n), "of CPU matches", a[0])
        for k, v in c.most_common(30):
            print(share(v), k)
    elif "--allocs" in args:
        site, own, n = collections.Counter(), collections.Counter(), 0
        for comm, p, fr in samples:
            if is_gc_thread(comm):
                continue
            hit = max((i for i, f in enumerate(fr[:12]) if ALLOC_SLOW.search(f)), default=-1)
            if hit < 0:
                continue
            n += p
            app = next((f for f in fr[hit + 1:] if group_of(f)), "?")
            site[app[:150]] += p
            own[group_of(app) or "?"] += p
        print(f"allocator slow path: {100 * n / total:.2f}% of CPU; shares below are of those samples")
        for k, v in own.most_common():
            print(f"{100 * v / n:6.1f}%  {k}")
        print()
        for k, v in site.most_common(40):
            print(f"{100 * v / n:6.1f}%  {k}")
    else:
        owner, kinds, by = collections.Counter(), collections.Counter(), collections.defaultdict(collections.Counter)
        for comm, p, fr in samples:
            o, k = owner_of(comm, fr), self_kind(fr[0], comm)
            owner[o] += p
            kinds[k] += p
            by[o][k] += p
        print(f"{len(samples)} samples\n\nCPU by owner")
        for n, v in owner.most_common():
            print(share(v), n)
            for k, x in by[n].most_common(5):
                print(f"            {100 * x / total:5.2f}  {k}")
        print("\nCPU by what the leaf frame is")
        for n, v in kinds.most_common():
            print(share(v), n)


if __name__ == "__main__":
    main()
