using System.Text.Json;
using Plinth.Core;

namespace Plinth.Tests;

public class GoldenTests
{
    private sealed record Golden(TrimRecord Trim, string Ground, bool PackShot, OutputRecord Output, string Dhash);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void Dhash_is_stable_and_distance_is_hamming()
    {
        var a = PerceptualHash.DHash(Synthetic.PackShot(400, 500, Rgb.Parse("#ffffff"), 100, 100, 150, 200, Rgb.Parse("#000000")));
        var b = PerceptualHash.DHash(Synthetic.PackShot(400, 500, Rgb.Parse("#ffffff"), 100, 100, 150, 200, Rgb.Parse("#000000"), quality: 60));
        var c = PerceptualHash.DHash(Synthetic.PackShot(400, 500, Rgb.Parse("#ffffff"), 250, 300, 60, 60, Rgb.Parse("#000000")));
        Assert.Equal(0, PerceptualHash.Distance(a, a));
        Assert.True(PerceptualHash.Distance(a, b) <= 2);
        Assert.True(PerceptualHash.Distance(a, c) > 6);
        Assert.Equal(16, PerceptualHash.ToHex(a).Length);
    }

    [Fact]
    public void Same_input_twice_gives_identical_bytes_and_record()
    {
        var bytes = Fixtures.All().First().Bytes;
        var a = Normalizer.Normalize(bytes, Recipe.Default, "https://x/a.jpg");
        var b = Normalizer.Normalize(bytes, Recipe.Default, "https://x/a.jpg");
        Assert.Equal(a.Output, b.Output);
        // Records hold lists (verdict reasons), so compare their JSON rather than rely on reference equality.
        Assert.Equal((a.Record with { TimingsMs = TimingsRecord.Zero }).ToJson(),
                     (b.Record with { TimingsMs = TimingsRecord.Zero }).ToJson());
    }

    [Fact]
    public void Every_fixture_matches_its_golden_record()
    {
        Directory.CreateDirectory(Fixtures.GoldenDir);
        var update = Environment.GetEnvironmentVariable("PLINTH_UPDATE_GOLDEN") == "1";
        var failures = new List<string>();

        foreach (var (name, bytes) in Fixtures.All())
        {
            var r = Normalizer.Normalize(bytes, Recipe.Default, "https://fixture/" + name);
            Assert.Equal("ok", r.Status);
            var actual = new Golden(r.Record.Trim, r.Record.Ground.Sampled, r.Record.Verdict.PackShot,
                r.Record.Output!, PerceptualHash.ToHex(PerceptualHash.DHash(r.Output!)));
            var path = Path.Combine(Fixtures.GoldenDir, Path.ChangeExtension(name, ".json"));

            if (update || !File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(actual, Json));
                File.WriteAllText(Path.Combine(SourceGoldenDir(), Path.GetFileName(path)), JsonSerializer.Serialize(actual, Json));
                continue;
            }

            var expected = JsonSerializer.Deserialize<Golden>(File.ReadAllText(path), Json)!;
            if (expected.Trim != actual.Trim) failures.Add($"{name}: trim {actual.Trim} != {expected.Trim}");
            if (Rgb.Parse(expected.Ground).Distance(Rgb.Parse(actual.Ground)) > 2) failures.Add($"{name}: ground {actual.Ground} != {expected.Ground}");
            if (expected.PackShot != actual.PackShot) failures.Add($"{name}: packShot {actual.PackShot} != {expected.PackShot}");
            if ((expected.Output.Width, expected.Output.Height) != (actual.Output.Width, actual.Output.Height)) failures.Add($"{name}: size");
            var d = PerceptualHash.Distance(Convert.ToUInt64(expected.Dhash, 16), Convert.ToUInt64(actual.Dhash, 16));
            if (d > 3) failures.Add($"{name}: dhash distance {d}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>The checked-in golden dir (not the bin copy), so updates land in git.</summary>
    private static string SourceGoldenDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Plinth.Tests.csproj"))) dir = dir.Parent;
        // The checked-in fixtures live at tests/fixtures, a SIBLING of tests/Plinth.Tests/
        // (not underneath it) — so go one level above the csproj's folder.
        var golden = Path.Combine(dir!.Parent!.FullName, "fixtures", "golden");
        Directory.CreateDirectory(golden);
        return golden;
    }
}
