using System.Diagnostics;
using System.Text.Json;
using Plinth.Core;

var args_ = args.ToList();
string Opt(string name, string def) { var i = args_.IndexOf(name); return i >= 0 && i + 1 < args_.Count ? args_[i + 1] : def; }
var fixtures = Opt("--fixtures", Path.Combine("tests", "fixtures", "src"));
var iterations = int.Parse(Opt("--iterations", "20"));
var baselinePath = Opt("--baseline", Path.Combine("docs", "bench", "baseline.json"));
var writeBaseline = args_.Contains("--write-baseline");
if (iterations < 1) { Console.Error.WriteLine("--iterations must be at least 1"); return 2; }

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
var files = Directory.EnumerateFiles(fixtures).OrderBy(f => f, StringComparer.Ordinal).ToList();
if (files.Count == 0) { Console.Error.WriteLine($"no fixtures in {fixtures}"); return 2; }

Engine.Init();
var proc = Process.GetCurrentProcess();
var results = new List<FixtureResult>();

foreach (var file in files)
{
    var bytes = File.ReadAllBytes(file);
    var name = Path.GetFileName(file);
    var warm = Normalizer.Normalize(bytes, Recipe.Default, "https://bench/" + name);
    if (warm.Status == "failed") { Console.Error.WriteLine($"{name}: {warm.Record.Error}"); continue; }

    var walls = new List<double>(iterations);
    proc.Refresh();
    var cpu0 = proc.TotalProcessorTime;
    var outBytes = 0;
    for (var i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        var r = Normalizer.Normalize(bytes, Recipe.Default, "https://bench/" + name);
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
var summary = new BenchSummary(
    DateTime.UtcNow.ToString("O"), Engine.Version, Engine.LibvipsVersion, Environment.ProcessorCount, iterations,
    Math.Round(results.Average(r => r.CpuMs), 1),
    Math.Round(results.Max(r => r.WallP95Ms), 1),
    Math.Round(proc.PeakWorkingSet64 / 1048576.0, 1),
    results.Max(r => r.OutputBytes),
    results);

Console.WriteLine($"{"fixture",-40} {"in KB",7} {"out KB",7} {"p50 ms",7} {"p95 ms",7} {"cpu ms",7}");
foreach (var r in results)
    Console.WriteLine($"{r.Name,-40} {r.InputBytes / 1024,7} {r.OutputBytes / 1024,7} {r.WallP50Ms,7} {r.WallP95Ms,7} {r.CpuMs,7}");
Console.WriteLine();
Console.WriteLine($"mean cpu/image {summary.MeanCpuMs} ms (budget 40)   max p95 {summary.MaxWallP95Ms} ms (budget 120)   peak RSS {summary.PeakWorkingSetMb} MB   max output {summary.MaxOutputBytes / 1024} KB (budget 80)");

Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
File.WriteAllText(Path.Combine(Path.GetDirectoryName(baselinePath)!, "latest.json"), JsonSerializer.Serialize(summary, json));

if (writeBaseline || !File.Exists(baselinePath))
{
    File.WriteAllText(baselinePath, JsonSerializer.Serialize(summary, json));
    Console.WriteLine($"baseline written to {baselinePath}");
    return 0;
}

var baseline = JsonSerializer.Deserialize<BenchSummary>(File.ReadAllText(baselinePath), json)!;
var regressions = new List<string>();
void Check(string what, double now, double before) { if (before > 0 && now > before * 1.2) regressions.Add($"{what}: {now} vs baseline {before}"); }
Check("mean cpu/image", summary.MeanCpuMs, baseline.MeanCpuMs);
Check("max p95", summary.MaxWallP95Ms, baseline.MaxWallP95Ms);
Check("max output bytes", summary.MaxOutputBytes, baseline.MaxOutputBytes);
if (regressions.Count > 0)
{
    Console.Error.WriteLine("REGRESSION\n" + string.Join("\n", regressions));
    return 1;
}
Console.WriteLine("within 20% of baseline");
return 0;

record FixtureResult(string Name, int InputBytes, int OutputBytes, double WallP50Ms, double WallP95Ms, double CpuMs);
record BenchSummary(string RunAt, string EngineVersion, string Libvips, int Cores, int Iterations,
    double MeanCpuMs, double MaxWallP95Ms, double PeakWorkingSetMb, int MaxOutputBytes, List<FixtureResult> Fixtures);
