using System.Diagnostics;
using System.Text.Json;
using Plinth.Core;

var args_ = args.ToList();
string Opt(string name, string def) { var i = args_.IndexOf(name); return i >= 0 && i + 1 < args_.Count ? args_[i + 1] : def; }

void Usage() => Console.Error.WriteLine(
    """
    usage: Plinth.Bench [options]
      --fixtures DIR      directory of source images (default tests/fixtures/src)
      --iterations N      timed runs per fixture (default 20)
      --concurrency N     libvips worker threads (default 1)
      --baseline PATH     baseline JSON to compare against (default docs/bench/baseline.json)
      --write-baseline    overwrite the baseline with this run
      --strict            also fail on mean CPU and max wall p95, not only output bytes
    """);

bool Number(string name, string def, out int value)
{
    if (int.TryParse(Opt(name, def), out value) && value >= 1) return true;
    Console.Error.WriteLine($"{name} must be a whole number of at least 1, got '{Opt(name, def)}'");
    Usage();
    return false;
}

var fixtures = Opt("--fixtures", Path.Combine("tests", "fixtures", "src"));
var baselinePath = Opt("--baseline", Path.Combine("docs", "bench", "baseline.json"));
var writeBaseline = args_.Contains("--write-baseline");
var strict = args_.Contains("--strict");
if (!Number("--iterations", "20", out var iterations)) return 2;
if (!Number("--concurrency", "1", out var concurrency)) return 2;

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
if (!Directory.Exists(fixtures)) { Console.Error.WriteLine($"no fixture directory at {fixtures}"); return 2; }
var files = Directory.EnumerateFiles(fixtures).OrderBy(f => f, StringComparer.Ordinal).ToList();
if (files.Count == 0) { Console.Error.WriteLine($"no fixtures in {fixtures}"); return 2; }

Engine.Init(concurrency);

// Card every fixture, including the ones the verdict calls editorial. The
// benchmark exists to measure the cost of the render path and the size of what
// the encoder produces; under the default policy an editorial image is handed
// straight back, which measures nothing and reports the source size as an
// output size.
var recipe = Recipe.Default with { Editorial = "card" };
var proc = Process.GetCurrentProcess();
var results = new List<FixtureResult>();

foreach (var file in files)
{
    var bytes = File.ReadAllBytes(file);
    var name = Path.GetFileName(file);
    var warm = Normalizer.Normalize(bytes, recipe, "https://bench/" + name);
    if (warm.Status == "failed") { Console.Error.WriteLine($"{name}: {warm.Record.Error}"); continue; }

    var walls = new List<double>(iterations);
    proc.Refresh();
    var cpu0 = proc.TotalProcessorTime;
    var outBytes = 0;
    for (var i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        var r = Normalizer.Normalize(bytes, recipe, "https://bench/" + name);
        walls.Add(sw.Elapsed.TotalMilliseconds);
        outBytes = r.Output!.Length;
    }
    proc.Refresh();
    var cpuPerImage = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / iterations;
    walls.Sort();
    var p95Index = Math.Clamp((int)Math.Ceiling(walls.Count * 0.95) - 1, 0, walls.Count - 1);
    results.Add(new FixtureResult(name, bytes.Length, outBytes,
        Math.Round(walls[walls.Count / 2], 1), Math.Round(walls[p95Index], 1), Math.Round(cpuPerImage, 1)));
}

if (results.Count == 0) { Console.Error.WriteLine("no fixture normalised successfully; nothing to benchmark"); return 2; }

proc.Refresh();
// PeakWorkingSet64 is not available on every platform; 0 means "not reported".
var peakMb = proc.PeakWorkingSet64 == 0 ? 0 : Math.Round(proc.PeakWorkingSet64 / 1048576.0, 1);
var summary = new BenchSummary(
    DateTime.UtcNow.ToString("O"), Engine.Version, Engine.LibvipsVersion, Environment.ProcessorCount,
    Engine.Concurrency, iterations,
    Math.Round(results.Average(r => r.CpuMs), 1),
    Math.Round(results.Max(r => r.WallP95Ms), 1),
    peakMb,
    results.Max(r => r.OutputBytes),
    results);

Console.WriteLine($"recipe {recipe.Hash} ({recipe.Canonical()})");
Console.WriteLine();
Console.WriteLine($"{"fixture",-40} {"in KB",7} {"out KB",7} {"p50 ms",7} {"p95 ms",7} {"cpu ms",7}");
foreach (var r in results)
    Console.WriteLine($"{r.Name,-40} {r.InputBytes / 1024,7} {r.OutputBytes / 1024,7} {r.WallP50Ms,7} {r.WallP95Ms,7} {r.CpuMs,7}");
Console.WriteLine();
var rss = peakMb == 0 ? "peak RSS unavailable" : $"peak RSS {peakMb} MB";
Console.WriteLine($"concurrency {summary.Concurrency}   mean cpu/image {summary.MeanCpuMs} ms (budget 40)   "
    + $"max wall p95 {summary.MaxWallP95Ms} ms (budget 120)   {rss}   "
    + $"max output {summary.MaxOutputBytes / 1024} KB (budget 80)");

// Path.GetDirectoryName returns "" for a bare filename, which is not a directory.
var baselineDir = Path.GetDirectoryName(baselinePath) is { Length: > 0 } d ? d : ".";
Directory.CreateDirectory(baselineDir);
File.WriteAllText(Path.Combine(baselineDir, "latest.json"), JsonSerializer.Serialize(summary, json));

if (writeBaseline || !File.Exists(baselinePath))
{
    File.WriteAllText(baselinePath, JsonSerializer.Serialize(summary, json));
    Console.WriteLine($"baseline written to {baselinePath}");
    return 0;
}

var baseline = JsonSerializer.Deserialize<BenchSummary>(File.ReadAllText(baselinePath), json)!;
var regressions = new List<string>();
var notes = new List<string>();
// Output bytes are deterministic, so they gate on their own. Wall and CPU times
// move with whatever else the machine is doing, so they only gate under --strict.
void Check(string what, double now, double before, bool gates)
{
    if (before <= 0 || now <= before * 1.2) return;
    (gates ? regressions : notes).Add($"{what}: {now} vs baseline {before}");
}
Check("mean cpu/image", summary.MeanCpuMs, baseline.MeanCpuMs, strict);
Check("max wall p95", summary.MaxWallP95Ms, baseline.MaxWallP95Ms, strict);
Check("max output bytes", summary.MaxOutputBytes, baseline.MaxOutputBytes, true);
if (notes.Count > 0)
    Console.WriteLine("over baseline but not gated (pass --strict to fail on these)\n" + string.Join("\n", notes));
if (regressions.Count > 0)
{
    Console.Error.WriteLine("REGRESSION\n" + string.Join("\n", regressions));
    return 1;
}
Console.WriteLine("within 20% of baseline");
return 0;

record FixtureResult(string Name, int InputBytes, int OutputBytes, double WallP50Ms, double WallP95Ms, double CpuMs);
record BenchSummary(string RunAt, string EngineVersion, string Libvips, int Cores, int Concurrency, int Iterations,
    double MeanCpuMs, double MaxWallP95Ms, double PeakWorkingSetMb, int MaxOutputBytes, List<FixtureResult> Fixtures);
