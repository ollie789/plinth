using System.Text.Json;
using NetVips;
using Plinth.Core;

namespace Plinth.Tests;

public class GoldenTests
{
    private sealed record Golden(TrimRecord Trim, string Ground, bool PackShot, OutputRecord Output, string Dhash);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");

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
            // Under the default recipe the verdict decides, so a fixture the
            // scorer calls editorial comes back untouched. The golden still
            // freezes what the renderer makes of it, so those are re-run with
            // the card policy.
            var byPolicy = Normalizer.Normalize(bytes, Recipe.Default, "https://fixture/" + name);
            if (!byPolicy.Record.Verdict.PackShot) Assert.Equal("editorial", byPolicy.Record.PassthroughReason);
            var r = byPolicy.Record.PassthroughReason == "editorial"
                ? Normalizer.Normalize(bytes, Recipe.Default with { Editorial = "card" }, "https://fixture/" + name)
                : byPolicy;
            Assert.Equal("ok", r.Status);
            var actual = new Golden(r.Record.Trim, r.Record.Ground.Sampled, r.Record.Verdict.PackShot,
                r.Record.Output!, PerceptualHash.ToHex(PerceptualHash.DHash(r.Output!)));
            var path = Path.Combine(Fixtures.GoldenDir, Path.ChangeExtension(name, ".json"));

            if (update)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(actual, Json));
                File.WriteAllText(Path.Combine(SourceGoldenDir(), Path.GetFileName(path)), JsonSerializer.Serialize(actual, Json));
                continue;
            }

            if (!File.Exists(path))
            {
                failures.Add($"{name}: no golden record at {path} (run with PLINTH_UPDATE_GOLDEN=1 to create it)");
                continue;
            }

            var expected = JsonSerializer.Deserialize<Golden>(File.ReadAllText(path), Json)!;
            // The trim box comes from a 512px working copy scaled back up, so a
            // pixel of rounding either way is expected; only a real shift matters.
            foreach (var (axis, want, got) in new[]
                     {
                         ("left", expected.Trim.Left, actual.Trim.Left),
                         ("top", expected.Trim.Top, actual.Trim.Top),
                         ("width", expected.Trim.Width, actual.Trim.Width),
                         ("height", expected.Trim.Height, actual.Trim.Height),
                     })
                if (Math.Abs(want - got) > 2) failures.Add($"{name}: trim {axis} {got} != {want}");
            if (expected.Trim.Noop != actual.Trim.Noop) failures.Add($"{name}: trim noop {actual.Trim.Noop} != {expected.Trim.Noop}");
            if (Math.Abs(expected.Trim.ContentShareBefore - actual.Trim.ContentShareBefore) > 0.01)
                failures.Add($"{name}: contentShareBefore {actual.Trim.ContentShareBefore} != {expected.Trim.ContentShareBefore}");
            if (Rgb.Parse(expected.Ground).Distance(Rgb.Parse(actual.Ground)) > 2) failures.Add($"{name}: ground {actual.Ground} != {expected.Ground}");
            if (expected.PackShot != actual.PackShot) failures.Add($"{name}: packShot {actual.PackShot} != {expected.PackShot}");
            if ((expected.Output.Width, expected.Output.Height) != (actual.Output.Width, actual.Output.Height)) failures.Add($"{name}: size");
            var d = PerceptualHash.Distance(Convert.ToUInt64(expected.Dhash, 16), Convert.ToUInt64(actual.Dhash, 16));
            if (d > 3) failures.Add($"{name}: dhash distance {d}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void An_exif_rotated_source_is_measured_and_rendered_in_display_space()
    {
        // Stored landscape 1000x800 with a landscape box; EXIF orientation 6 turns
        // it 90 degrees clockwise, so the DISPLAYED frame is 800x1000 and the
        // displayed box is portrait at (450, 300, 200, 400).
        var stored = Synthetic.PackShot(1000, 800, White, 300, 150, 400, 200, Black);
        byte[] rotated;
        using (var src = Image.NewFromBuffer(stored))
        using (var tagged = src.Mutate(m => m.Set(GValue.GIntType, "orientation", 6)))
            rotated = tagged.JpegsaveBuffer(q: 95, keep: Enums.ForeignKeep.All);

        var info = SourceInspector.Inspect(rotated);
        Assert.Equal(6, info.Orientation);
        Assert.Equal((1000, 800), (info.Width, info.Height));

        var recipe = Recipe.Default with { Format = "png" };
        var m = Measurer.Measure(rotated, info, recipe);
        Assert.InRange(m.Box.Left, 444, 456);
        Assert.InRange(m.Box.Top, 294, 306);
        Assert.InRange(m.Box.Width, 194, 212);
        Assert.InRange(m.Box.Height, 394, 412);

        var r = Renderer.Render(rotated, info, m, recipe, recipe.Background);
        using var img = Image.NewFromBuffer(r.Bytes);
        var t = img.FindTrim(threshold: 12, background: [255, 255, 255]).Select(Convert.ToInt32).ToArray();
        // Same aspect as the rotated box - taller than wide - and centred.
        Assert.True(t[3] > t[2], $"content {t[2]}x{t[3]} is not taller than wide");
        Assert.InRange(t[2] / (double)t[3], m.Box.Width / (double)m.Box.Height - 0.03,
            m.Box.Width / (double)m.Box.Height + 0.03);
        Assert.InRange(t[3], 960, 980);
        Assert.InRange(t[0] + t[2] / 2.0, 490, 510);
        Assert.InRange(t[1] + t[3] / 2.0, 615, 635);
    }

    [Fact]
    public void An_empty_buffer_fails_without_throwing()
    {
        var r = Normalizer.Normalize([], Recipe.Default);
        Assert.Equal("failed", r.Status);
        Assert.Null(r.Output);
        Assert.NotNull(r.Record.Error);
    }

    [Fact]
    public void A_multi_page_gif_reports_its_page_count_and_one_page_of_height()
    {
        using var a = (Image.Black(60, 80, bands: 3) + White.ToVips())
            .Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        using var b = (Image.Black(60, 80, bands: 3) + Black.ToVips())
            .Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        using var joined = Image.Arrayjoin([a, b], across: 1);
        using var tagged = joined.Mutate(m =>
        {
            m.Set(GValue.GIntType, "page-height", 80);
            m.Set(GValue.GIntType, "n-pages", 2);
        });

        var info = SourceInspector.Inspect(tagged.GifsaveBuffer());
        Assert.Equal("gif", info.Format);
        Assert.Equal(2, info.Pages);
        Assert.Equal(80, info.Height);
        Assert.Equal(60, info.Width);
    }

    [Fact]
    public void A_flat_source_skips_the_crop_and_fills_the_canvas()
    {
        var r = Normalizer.Normalize(Synthetic.Flat(800, 1000, White), Recipe.Default with { Format = "png" });
        Assert.Equal("ok", r.Status);
        Assert.True(r.Record.Trim.Noop);
        Assert.Equal((1000, 1250), (r.Record.Output!.Width, r.Record.Output.Height));
        using var img = Image.NewFromBuffer(r.Output!);
        Assert.Equal((1000, 1250), (img.Width, img.Height));
        // Nothing to trim: the whole canvas is the flat colour.
        Assert.Equal(255.0, img.Min());
        Assert.Equal(255.0, img.Max());
    }

    [Fact]
    public void Failed_and_passthrough_records_round_trip_through_json()
    {
        var failed = Normalizer.Normalize("nope"u8.ToArray(), Recipe.Default, "https://a/b.jpg").Record;
        Assert.Equal("failed", failed.Status);
        Assert.Null(failed.Output);
        Assert.NotNull(failed.Error);

        var first = Normalizer.Normalize(Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black), Recipe.Default);
        var passthrough = Normalizer.Normalize(first.Output!, Recipe.Default).Record;
        Assert.Equal("passthrough", passthrough.Status);

        foreach (var record in new[] { failed, passthrough })
        {
            var back = ResultRecord.FromJson(record.ToJson());
            Assert.Equal(record.ToJson(), back.ToJson());
            Assert.Equal(record.Key, back.Key);
            Assert.Equal(record.Status, back.Status);
            Assert.Equal(record.Error, back.Error);
            Assert.Equal(record.Output?.Bytes, back.Output?.Bytes);
            Assert.Equal(record.Verdict.Reasons, back.Verdict.Reasons);
        }
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
