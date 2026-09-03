using System.CommandLine;
using System.Text.Json;
using Plinth.Cli;
using Plinth.Core;
using Plinth.Pipeline.Stores;
using Plinth.Tests.Fakes;

namespace Plinth.Tests.Cli;

public class CliTests : IDisposable
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plinth-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string _out;

    public CliTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "in"));
        _out = Path.Combine(_dir, "out");
        for (var i = 0; i < 3; i++)
            File.WriteAllBytes(Path.Combine(_dir, "in", $"p{i}.jpg"), Synthetic.PackShot(600 + i * 10, 800, White, 100, 100, 200, 300, Black));
        File.WriteAllBytes(Path.Combine(_dir, "in", "junk.jpg"), "not an image"u8.ToArray());
    }

    private static async Task<(int Code, List<JsonElement> Lines)> Run(RootCommand root, params string[] args)
    {
        var manifest = Path.Combine(Path.GetTempPath(), "plinth-manifest-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var code = await root.Parse([.. args, "--manifest", manifest]).InvokeAsync();
        var lines = File.Exists(manifest)
            ? File.ReadAllLines(manifest).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList()
            : [];
        return (code, lines);
    }

    private static int Count(List<JsonElement> lines, string status) => lines.Count(l => l.GetProperty("status").GetString() == status);

    [Fact]
    public async Task Run_over_a_directory_normalises_once_and_skips_on_rerun()
    {
        var root = CliApp.Build();
        var first = await Run(root, "run", "--input", Path.Combine(_dir, "in"), "--output", _out, "--concurrency", "2");
        Assert.Equal(0, first.Code);
        Assert.Equal(4, first.Lines.Count);
        Assert.Equal(3, Count(first.Lines, "ok"));
        Assert.Equal(1, Count(first.Lines, "failed"));
        Assert.Equal(3, Directory.GetFiles(_out, "*.webp", SearchOption.AllDirectories).Length);

        // A processed item's line is the whole result record, so the manifest doubles as the
        // performance and verdict report without anyone re-reading the store.
        var okLine = first.Lines.First(l => l.GetProperty("status").GetString() == "ok");
        Assert.True(okLine.GetProperty("verdict").TryGetProperty("packShot", out _));
        Assert.True(okLine.GetProperty("timingsMs").GetProperty("total").GetInt64() >= 0);
        Assert.True(okLine.GetProperty("output").GetProperty("bytes").GetInt32() > 0);

        var second = await Run(root, "run", "--input", Path.Combine(_dir, "in"), "--output", _out);
        Assert.Equal(0, second.Code);
        Assert.Equal(3, Count(second.Lines, "skipped"));
        Assert.Equal(1, Count(second.Lines, "failed"));
    }

    [Fact]
    public async Task Run_over_a_url_list_upgrades_thumbnail_urls_to_their_master()
    {
        const string master = "https://m.media-amazon.com/images/I/71abcDEF.jpg";
        var fetcher = new FakeFetcher().With(master, Synthetic.PackShot(600, 800, White, 100, 100, 200, 300, Black));
        var list = Path.Combine(_dir, "amazon.txt");
        File.WriteAllLines(list, ["https://m.media-amazon.com/images/I/71abcDEF._AC_SY445_.jpg"]);
        Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", "m.media-amazon.com");
        try
        {
            var r = await Run(CliApp.Build(_ => fetcher), "run", "--input", list, "--output", _out);
            Assert.Equal(0, r.Code);
            Assert.Equal(1, Count(r.Lines, "ok"));
            Assert.Equal(1, fetcher.Calls);
            Assert.Equal(master, r.Lines[0].GetProperty("sourceId").GetString());
        }
        finally { Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", null); }
    }

    [Fact]
    public async Task Two_spellings_of_one_amazon_source_are_deduped_to_a_single_item()
    {
        const string master = "https://m.media-amazon.com/images/I/71abcDEF.jpg";
        var fetcher = new FakeFetcher().With(master, Synthetic.PackShot(600, 800, White, 100, 100, 200, 300, Black));
        var list = Path.Combine(_dir, "amazon-dupes.txt");
        File.WriteAllLines(list,
        [
            "https://m.media-amazon.com/images/I/71abcDEF._AC_SY445_.jpg",
            "https://m.media-amazon.com/images/I/71abcDEF._SL500_.jpg",
            master,
        ]);
        Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", "m.media-amazon.com");
        try
        {
            var r = await Run(CliApp.Build(_ => fetcher), "run", "--input", list, "--output", _out);
            Assert.Equal(0, r.Code);
            Assert.Single(r.Lines);
            Assert.Equal(1, fetcher.Calls);
            Assert.Equal(master, r.Lines[0].GetProperty("sourceId").GetString());
        }
        finally { Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", null); }
    }

    [Fact]
    public async Task Run_over_a_url_list_skips_by_key_without_fetching_and_refresh_refetches()
    {
        var fetcher = new FakeFetcher()
            .With("https://cdn.example.com/a.jpg", Synthetic.PackShot(600, 800, White, 100, 100, 200, 300, Black))
            .With("https://cdn.example.com/b.jpg", Synthetic.PackShot(640, 800, White, 100, 100, 200, 300, Black));
        var list = Path.Combine(_dir, "urls.txt");
        File.WriteAllLines(list, ["https://cdn.example.com/b.jpg", "https://cdn.example.com/a.jpg", "https://cdn.example.com/missing.jpg"]);
        Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", "cdn.example.com");
        try
        {
            var root = CliApp.Build(_ => fetcher);
            var first = await Run(root, "run", "--input", list, "--output", _out);
            Assert.Equal(0, first.Code);
            Assert.Equal(2, Count(first.Lines, "ok"));
            Assert.Equal(1, Count(first.Lines, "failed"));
            Assert.Equal(3, fetcher.Calls);

            var second = await Run(root, "run", "--input", list, "--output", _out);
            Assert.Equal(2, Count(second.Lines, "skipped"));
            Assert.Equal(4, fetcher.Calls);   // only the failed one is retried

            var refreshed = await Run(root, "run", "--input", list, "--output", _out, "--refresh");
            Assert.Equal(2, Count(refreshed.Lines, "unchanged"));
            Assert.Equal(7, fetcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", null); }
    }

    [Fact]
    public async Task Key_inspect_and_version_print_and_bad_usage_exits_two()
    {
        var stdout = new StringWriter();
        var root = CliApp.Build(stdout: stdout);
        var file = Path.Combine(_dir, "in", "p0.jpg");
        Assert.Equal(0, await root.Parse(["key", file]).InvokeAsync());
        Assert.Equal(64, stdout.ToString().Trim().Length);

        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await root.Parse(["inspect", file]).InvokeAsync());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());

        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await root.Parse(["version"]).InvokeAsync());
        Assert.Contains(Engine.Version, stdout.ToString());

        Assert.Equal(2, await root.Parse(["run", "--input", Path.Combine(_dir, "nope"), "--output", _out]).InvokeAsync());
        Assert.NotEqual(0, await root.Parse(["run"]).InvokeAsync());
    }

    [Fact]
    public async Task Url_list_starting_with_a_comment_line_is_still_detected_as_a_url_list()
    {
        var fetcher = new FakeFetcher()
            .With("https://cdn.example.com/a.jpg", Synthetic.PackShot(600, 800, White, 100, 100, 200, 300, Black))
            .With("https://cdn.example.com/b.jpg", Synthetic.PackShot(640, 800, White, 100, 100, 200, 300, Black));
        var list = Path.Combine(_dir, "commented-urls.txt");
        File.WriteAllLines(list, ["# a leading comment", "https://cdn.example.com/a.jpg", "https://cdn.example.com/b.jpg"]);
        Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", "cdn.example.com");
        try
        {
            var root = CliApp.Build(_ => fetcher);
            var result = await Run(root, "run", "--input", list, "--output", _out);
            Assert.Equal(0, result.Code);
            Assert.Equal(2, Count(result.Lines, "ok"));
            Assert.Equal(2, fetcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", null); }
    }

    [Fact]
    public async Task Url_list_with_a_repeated_url_is_deduplicated()
    {
        var fetcher = new FakeFetcher()
            .With("https://cdn.example.com/a.jpg", Synthetic.PackShot(600, 800, White, 100, 100, 200, 300, Black));
        var list = Path.Combine(_dir, "dup-urls.txt");
        File.WriteAllLines(list, ["https://cdn.example.com/a.jpg", "https://cdn.example.com/a.jpg"]);
        Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", "cdn.example.com");
        try
        {
            var root = CliApp.Build(_ => fetcher);
            var result = await Run(root, "run", "--input", list, "--output", _out);
            Assert.Equal(0, result.Code);
            Assert.Single(result.Lines);
            Assert.Equal(1, Count(result.Lines, "ok"));
            Assert.Equal(1, fetcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", null); }
    }

    [Fact]
    public async Task Key_on_a_missing_path_exits_two_and_prints_a_message_to_stderr()
    {
        var root = CliApp.Build();
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            var code = await root.Parse(["key", Path.Combine(_dir, "nope.jpg")]).InvokeAsync();
            Assert.Equal(2, code);
            Assert.Contains("plinth key:", stderr.ToString());
        }
        finally { Console.SetError(original); }
    }

    [Fact]
    public async Task An_unreadable_file_fails_only_its_own_line_and_the_run_still_succeeds()
    {
        var dir = Path.Combine(_dir, "locked");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 2; i++)
            File.WriteAllBytes(Path.Combine(dir, $"q{i}.jpg"), Synthetic.PackShot(500 + i * 10, 700, White, 80, 80, 150, 200, Black));
        var locked = Path.Combine(dir, "zz-locked.jpg");
        File.WriteAllBytes(locked, Synthetic.PackShot(520, 700, White, 80, 80, 150, 200, Black));
        if (OperatingSystem.IsWindows()) return;   // no mode bits to take away

        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var root = CliApp.Build();
            var result = await Run(root, "run", "--input", dir, "--output", Path.Combine(_dir, "locked-out"));
            Assert.Equal(0, result.Code);
            Assert.Equal(3, result.Lines.Count);
            Assert.Equal(2, Count(result.Lines, "ok"));
            Assert.Equal(1, Count(result.Lines, "failed"));
            Assert.NotNull(result.Lines.Single(l => l.GetProperty("status").GetString() == "failed").GetProperty("error").GetString());
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
