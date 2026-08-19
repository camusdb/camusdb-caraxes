/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text;
using System.Text.Json;
using Caraxes.Core.Scenario;

namespace Caraxes.Core.Matrix;

/// <summary>One cell's outcome in a sweep: its coordinates and the scenario verdict.</summary>
public sealed record MatrixCellResult(string Name, IReadOnlyDictionary<string, string> Coordinates, ScenarioVerdict Verdict);

/// <summary>
/// Runs a matrix sweep: expands the cells, builds the shared image once, then runs each cell in turn
/// through the ordinary <see cref="ScenarioRunner"/> (so a cell behaves exactly like a hand-written
/// scenario), and writes a cross-cell report. Cells run sequentially — a laptop cannot host several
/// clusters at once, and sequential runs keep the fault behavior of one cell from perturbing another.
/// </summary>
public sealed class MatrixRunner
{
    private readonly MatrixSpec matrix;

    private readonly string runRoot;

    private readonly string matrixDir;

    public MatrixRunner(MatrixSpec matrix, string? runRoot = null)
    {
        this.matrix = matrix;
        this.runRoot = runRoot ?? Path.Combine(Environment.CurrentDirectory, "runs");
        matrixDir = Path.Combine(this.runRoot, "matrix", matrix.Name);
    }

    public async Task<IReadOnlyList<MatrixCellResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MatrixCell> cells = MatrixExpander.Expand(matrix);
        Directory.CreateDirectory(matrixDir);

        Console.WriteLine($"==> matrix '{matrix.Name}': {cells.Count} cell(s)");
        foreach (MatrixCell cell in cells)
            Console.WriteLine($"    - {cell.Name}");

        List<MatrixCellResult> results = [];
        for (int i = 0; i < cells.Count; i++)
        {
            MatrixCell cell = cells[i];
            Console.WriteLine();
            Console.WriteLine($"===== cell {i + 1}/{cells.Count}: {cell.Name} =====");

            // Build the shared image only on the first cell; every later cell reuses it (cells differ
            // only in runtime config). A cell that itself fails still lets the sweep continue.
            ScenarioRunner runner = new(cell.Scenario, Path.Combine(matrixDir, "cells"));
            ScenarioVerdict verdict;
            try
            {
                verdict = await runner.RunAsync(skipBuild: i > 0, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                verdict = new ScenarioVerdict(false, -1, false, false, cell.Name, [$"cell threw: {e.Message}"]);
            }

            results.Add(new MatrixCellResult(cell.Name, cell.Coordinates, verdict));
        }

        WriteReport(results);
        WriteJson(results);
        PrintSummary(results);
        return results;
    }

    private void WriteReport(IReadOnlyList<MatrixCellResult> results)
    {
        List<string> axisKeys = results
            .SelectMany(r => r.Coordinates.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        StringBuilder sb = new();
        sb.AppendLine($"# Matrix report — {matrix.Name}");
        sb.AppendLine();
        int passed = results.Count(r => r.Verdict.Passed);
        sb.AppendLine($"**{passed}/{results.Count} cells passed.**");
        sb.AppendLine();

        sb.Append("| cell |");
        foreach (string k in axisKeys)
            sb.Append($" {k} |");
        sb.AppendLine(" verdict | max recovery (s) | latency inflation | notes |");

        sb.Append("|---|");
        foreach (string _ in axisKeys)
            sb.Append("---|");
        sb.AppendLine("---|---|---|---|");

        foreach (MatrixCellResult r in results)
        {
            sb.Append($"| {r.Name} |");
            foreach (string k in axisKeys)
                sb.Append($" {(r.Coordinates.TryGetValue(k, out string? v) ? v : "-")} |");

            string recovery = r.Verdict.Analysis is null ? "-" : $"{r.Verdict.Analysis.MaxRecoverySeconds:N1}";
            string inflation = r.Verdict.Analysis is null ? "-" : $"{r.Verdict.Analysis.LatencyInflation:N1}x";
            string note = r.Verdict.Passed
                ? "ok"
                : FirstFailureNote(r.Verdict);

            sb.AppendLine($" {(r.Verdict.Passed ? "PASS" : "FAIL")} | {recovery} | {inflation} | {Escape(note)} |");
        }

        File.WriteAllText(Path.Combine(matrixDir, "matrix-report.md"), sb.ToString());
    }

    private static string FirstFailureNote(ScenarioVerdict verdict)
    {
        // Prefer an explicit CHECK FAILED / reconciliation / crash note over the generic metrics line.
        string? failure = verdict.Notes.FirstOrDefault(n =>
            n.Contains("CHECK FAILED") || n.Contains("reconciliation failure") || n.Contains("crashed") || n.Contains("init failed") || n.Contains("cell threw"));
        return failure ?? verdict.Notes.LastOrDefault() ?? "failed";
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ").Trim();

    private void WriteJson(IReadOnlyList<MatrixCellResult> results)
    {
        var doc = new
        {
            matrix = matrix.Name,
            passed = results.Count(r => r.Verdict.Passed),
            total = results.Count,
            cells = results.Select(r => new
            {
                r.Name,
                r.Coordinates,
                r.Verdict.Passed,
                r.Verdict.WorkloadExitCode,
                r.Verdict.ReconciliationPassed,
                maxRecoverySeconds = r.Verdict.Analysis?.MaxRecoverySeconds,
                latencyInflation = r.Verdict.Analysis?.LatencyInflation,
                r.Verdict.Notes,
            }),
        };

        File.WriteAllText(
            Path.Combine(matrixDir, "matrix.json"),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void PrintSummary(IReadOnlyList<MatrixCellResult> results)
    {
        int passed = results.Count(r => r.Verdict.Passed);
        Console.WriteLine();
        Console.WriteLine($"===== matrix '{matrix.Name}': {passed}/{results.Count} cells passed =====");
        foreach (MatrixCellResult r in results)
            Console.WriteLine($"  {(r.Verdict.Passed ? "PASS" : "FAIL")}  {r.Name}");
        Console.WriteLine($"report: {Path.Combine(matrixDir, "matrix-report.md")}");
    }
}
