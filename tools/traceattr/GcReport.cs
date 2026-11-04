using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

/// <summary>
/// Per-collection view of a trace taken with the GC keyword (Microsoft-Windows-DotNETRuntime:0x1:4): for every
/// collection the generation, reason, pause, the bytes promoted out of gen0 and gen1, the gen0 budget the GC chose
/// for the next cycle (FinalYoungestDesired, summed over heaps as GCPerHeapHistory reports it) and the heap count,
/// then a summary by generation. Written to read why two builds of the same node differ in gen0/gen1 frequency.
/// </summary>
static class GcReport
{
    public static void Run(TraceLog log)
    {
        TraceLogEventSource source = log.Events.GetSource();
        var clr = source.Clr;
        var rows = new List<Row>();
        Row? current = null;
        clr.GCStart += d => { current = new Row { Count = d.Count, Gen = d.Depth, Reason = d.Reason.ToString(), Type = d.Type.ToString(), StartMs = d.TimeStampRelativeMSec }; rows.Add(current); };
        clr.GCPerHeapHistory += d =>
        {
            if (current is null) return;
            current.Heaps++;
            current.Gen0Budget += d.GenData(Gens.Gen0).Budget;
            current.Gen0NewAlloc += d.GenData(Gens.Gen0).ObjSpaceBefore;
            current.Gen0Out += d.GenData(Gens.Gen0).Out;
            current.Gen1Out += d.GenData(Gens.Gen1).Out;
            current.Gen0SizeBefore += d.GenData(Gens.Gen0).SizeBefore;
            current.Gen0SurvSum += d.GenData(Gens.Gen0).SurvRate; 
        };
        clr.GCGlobalHeapHistory += d => { if (current is null) return; current.NumHeaps = d.NumHeaps; current.FinalYoungestDesired = d.FinalYoungestDesired; current.Gen0Reduction = d.Gen0ReductionCount; current.Condemned = d.CondemnedGeneration; };
        clr.GCHeapStats += d =>
        {
            if (current is null) return;
            current.Promoted0 = d.TotalPromotedSize0; current.Promoted1 = d.TotalPromotedSize1; current.Promoted2 = d.TotalPromotedSize2;
            current.Size0 = d.GenerationSize0; current.Size1 = d.GenerationSize1; current.Size2 = d.GenerationSize2; current.Size3 = d.GenerationSize3;
        };
        clr.GCStop += d => { if (current is not null && current.Count == d.Count) current.StopMs = d.TimeStampRelativeMSec; };
        double first = double.MaxValue, last = 0;
        source.AllEvents += d => { if (d.TimeStampRelativeMSec < first) first = d.TimeStampRelativeMSec; if (d.TimeStampRelativeMSec > last) last = d.TimeStampRelativeMSec; };
        source.Process();
        double seconds = (last - first) / 1000.0;
        Console.WriteLine($"trace {seconds:F1} s, {rows.Count} collections");
        Console.WriteLine("count gen reason                 pause_ms  heaps gen0_budget_MB gen0_desired_MB gen0_alloc_MB promoted0_MB promoted1_MB gen1_size_MB gen2_size_MB loh_size_MB survrate0");
        foreach (Row r in rows)
            Console.WriteLine($"{r.Count,5} {r.Gen,3} {r.Reason,-22} {r.PauseMs,8:F1} {r.NumHeaps,5} {r.Gen0Budget / 1048576.0,14:F0} {r.FinalYoungestDesired * Math.Max(1, r.NumHeaps) / 1048576.0,15:F0} {r.Gen0NewAlloc / 1048576.0,13:F0} {r.Promoted0 / 1048576.0,12:F1} {r.Promoted1 / 1048576.0,12:F1} {r.Size1 / 1048576.0,12:F0} {r.Size2 / 1048576.0,12:F0} {r.Size3 / 1048576.0,11:F0} {(r.Heaps > 0 ? r.Gen0SurvSum / r.Heaps : 0),9:F1}");
        Console.WriteLine();
        foreach (var g in rows.GroupBy(r => r.Gen).OrderBy(g => g.Key))
        {
            var l = g.ToList();
            Console.WriteLine($"gen{g.Key}: {l.Count} collections ({l.Count / seconds:F2}/s), pause mean {l.Average(r => r.PauseMs):F1} ms sum {l.Sum(r => r.PauseMs):F0} ms ({l.Sum(r => r.PauseMs) / seconds / 10:F2}% of wall), " +
                              $"gen0 budget mean {l.Average(r => r.Gen0Budget) / 1048576.0:F0} MB, gen0 alloc since last mean {l.Average(r => r.Gen0NewAlloc) / 1048576.0:F0} MB, promoted0 mean {l.Average(r => r.Promoted0) / 1048576.0:F1} MB, promoted1 mean {l.Average(r => r.Promoted1) / 1048576.0:F1} MB, heaps {l.Max(r => r.NumHeaps)}");
        }
        var reasons = rows.GroupBy(r => r.Gen + "/" + r.Reason).OrderByDescending(g => g.Count()).Take(8);
        Console.WriteLine("reasons: " + string.Join(", ", reasons.Select(g => $"{g.Key} x{g.Count()}")));
    }

    sealed class Row
    {
        public int Count, Gen, NumHeaps, Heaps, Gen0Reduction, Condemned;
        public string Reason = "", Type = "";
        public double StartMs, StopMs, Gen0SurvSum;
        public long Gen0Budget, Gen0NewAlloc, Gen0Out, Gen1Out, Gen0SizeBefore, FinalYoungestDesired, Promoted0, Promoted1, Promoted2, Size0, Size1, Size2, Size3;
        public double PauseMs => StopMs > 0 ? StopMs - StartMs : 0;
    }
}
