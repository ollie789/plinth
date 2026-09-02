using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;

namespace Plinth.Cli;

/// <summary>`plinth run`: fetch stage → process stage, skip by key, one manifest line per item.</summary>
public static class RunCommand
{
    private const int FetchWorkers = 8;

    private sealed record Item(string Display, string? Url, string? Path);
    private sealed record Fetched(Item Item, string SourceId, string Key, byte[]? Bytes, string? Skip, string? Error);
    /// <summary>
    /// The shape for items that never reached the pixels. Everything the pipeline actually ran
    /// on is written as its full <see cref="ResultRecord"/> instead, so a manifest line carries
    /// the verdict and the timings without a second pass over the store.
    /// </summary>
    private sealed record SkipLine(string Key, string SourceId, string Status);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Command Build(Func<PipelineOptions, ISourceFetcher> fetchers, TextWriter stdout)
    {
        var input = new Option<string>("--input", "-i") { Description = "Directory of images, a file of URLs (- for stdin), or one image", Required = true };
        var output = new Option<string>("--output", "-o") { Description = "Output directory, or a store URI (azblob://container, none)", Required = true };
        var recipe = new Option<string?>("--recipe") { Description = "Recipe name from PLINTH_RECIPES, or a recipe JSON file" };
        var concurrency = new Option<int>("--concurrency") { Description = "Process workers", DefaultValueFactory = _ => Environment.ProcessorCount };
        var manifest = new Option<string?>("--manifest") { Description = "Write one JSON line per item here (default stdout)" };
        var refresh = new Option<bool>("--refresh") { Description = "Refetch sources whose key exists and reprocess only if the bytes changed" };

        var cmd = new Command("run", "Normalise a batch of sources into a store") { input, output, recipe, concurrency, manifest, refresh };
        cmd.SetAction(async (pr, ct) =>
        {
            try
            {
                var options = PipelineOptions.FromEnvironment(Environment.GetEnvironmentVariable);
                var rec = ResolveRecipe(pr.GetValue(recipe), options.Recipes);
                var store = OpenOutput(pr.GetValue(output)!);
                var items = ReadItems(pr.GetValue(input)!);
                Engine.Init(1);
                var workers = Math.Max(1, pr.GetValue(concurrency));

                // A manifest path owns its writer and is disposed here; the stdout fallback is
                // owned by the caller (Console.Out, or the TextWriter CliApp.Build was given for
                // tests) and must never be disposed — only flushed — or later commands in the
                // same process would find stdout closed underneath them.
                var manifestPath = pr.GetValue(manifest);
                var manifestWriter = manifestPath is null ? stdout : new StreamWriter(manifestPath, append: false);
                try
                {
                    var counts = await RunAsync(items, rec, store, fetchers(options), workers, pr.GetValue(refresh), manifestWriter, ct);
                    Console.Error.WriteLine($"{items.Count} items: {counts.GetValueOrDefault("ok")} ok, {counts.GetValueOrDefault("passthrough")} passthrough, {counts.GetValueOrDefault("failed")} failed, {counts.GetValueOrDefault("skipped")} skipped, {counts.GetValueOrDefault("unchanged")} unchanged");
                }
                finally
                {
                    if (manifestPath is null) await manifestWriter.FlushAsync(ct);
                    else await manifestWriter.DisposeAsync();
                }
                return 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"plinth run: {Innermost(e)}");
                return 2;
            }
        });
        return cmd;
    }

    /// <summary>
    /// A fault inside the parallel stages arrives wrapped; the wrapper says nothing useful, so
    /// report the message of the exception that actually failed.
    /// </summary>
    private static string Innermost(Exception e) =>
        e is AggregateException agg && agg.Flatten().InnerExceptions.Count > 0
            ? Innermost(agg.Flatten().InnerExceptions[0])
            : e.Message;

    public static Recipe ResolveRecipe(string? nameOrPath, RecipeCatalog catalog)
    {
        if (nameOrPath is not null && File.Exists(nameOrPath)) return Recipe.FromJson(File.ReadAllText(nameOrPath));
        return catalog.Get(nameOrPath);
    }

    private static IOutputStore OpenOutput(string output) =>
        output == "none" || output.Contains("://", StringComparison.Ordinal)
            ? StoreUri.Open(output, Environment.GetEnvironmentVariable)
            : new FileSystemStore(output);

    private static List<Item> ReadItems(string input)
    {
        if (input == "-")
            return UrlItems(Console.In.ReadToEnd().Split('\n'));
        if (Directory.Exists(input))
            return Directory.EnumerateFiles(input).Order(StringComparer.Ordinal).Select(f => new Item(f, null, f)).ToList();
        if (!File.Exists(input)) throw new PlinthException($"input not found: {input}");
        return LooksLikeUrlList(input) ? UrlItems(File.ReadAllLines(input)) : [new Item(input, null, input)];
    }

    /// <summary>
    /// A URL list is decided from its first non-blank, non-comment line, sniffed from only the
    /// first 4 KB as text — enough to see past a leading "# ..." comment without ever decoding a
    /// whole (possibly large, possibly binary) image file as text just to classify it.
    /// </summary>
    private static bool LooksLikeUrlList(string path)
    {
        var buffer = new char[4096];
        int read;
        using (var reader = new StreamReader(path))
            read = reader.Read(buffer, 0, buffer.Length);
        foreach (var line in new string(buffer, 0, read).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static List<Item> UrlItems(IEnumerable<string> lines)
    {
        var candidates = lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(u => new Item(u, u, null));

        // Dedupe by canonical source id, keeping the first occurrence, so the same image
        // isn't fetched and normalised twice just because it appeared twice in the list. A
        // URL that fails to parse can't be canonicalised, so it is left alone — it survives
        // as its own item and surfaces as its own failed record downstream.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<Item>();
        foreach (var item in candidates)
        {
            string? canonical;
            try { canonical = SourceId.FromUrl(item.Url!); }
            catch (PlinthException) { canonical = null; }
            if (canonical is null || seen.Add(canonical))
                deduped.Add(item);
        }

        return deduped
            .OrderBy(i => Uri.TryCreate(i.Url, UriKind.Absolute, out var x) ? x.Host : "", StringComparer.Ordinal)
            .ThenBy(i => i.Url, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<Dictionary<string, int>> RunAsync(List<Item> items, Recipe recipe, IOutputStore store, ISourceFetcher fetcher,
        int workers, bool refresh, TextWriter manifest, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>();
        var manifestLock = new object();

        void Emit(string status, string json)
        {
            lock (manifestLock)
            {
                manifest.WriteLine(json);
                counts[status] = counts.GetValueOrDefault(status) + 1;
            }
        }

        void EmitRecord(ResultRecord record) => Emit(record.Status, record.ToJson());
        void EmitSkip(string key, string sourceId, string status) =>
            Emit(status, JsonSerializer.Serialize(new SkipLine(key, sourceId, status), Json));

        var fetched = Channel.CreateBounded<Fetched>(new BoundedChannelOptions(workers * 2) { SingleWriter = false });
        var todo = Channel.CreateUnbounded<Item>();
        foreach (var i in items) todo.Writer.TryWrite(i);
        todo.Writer.Complete();

        var fetchStage = Enumerable.Range(0, FetchWorkers).Select(_ => Task.Run(async () =>
        {
            await foreach (var item in todo.Reader.ReadAllAsync(ct))
                await fetched.Writer.WriteAsync(await FetchOneAsync(item, recipe, store, fetcher, refresh, ct), ct);
        }, ct)).ToArray();
        // Whether the fetch workers all finish cleanly or one of them throws, the channel must
        // still complete so the process stage's ReadAllAsync below does not hang forever; a
        // faulted worker's exception is attached to the completion and surfaces from
        // fetched.Reader.ReadAllAsync(ct) in the process stage, which is what should fail the run.
        var closer = Task.Run(async () =>
        {
            try { await Task.WhenAll(fetchStage); fetched.Writer.Complete(); }
            catch (Exception e) { fetched.Writer.Complete(e); }
        }, ct);

        await Parallel.ForEachAsync(fetched.Reader.ReadAllAsync(ct), new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, async (f, token) =>
        {
            if (f.Skip is not null) { EmitSkip(f.Key, f.SourceId, f.Skip); return; }

            // One item's bad luck is that item's manifest line, never the batch's exit code:
            // a corrupt file, a full disk or a store that rejects one put all land here.
            try
            {
                if (f.Error is not null) { EmitRecord(ResultRecord.Failed(f.Key, f.SourceId, recipe, f.Error)); return; }
                var result = Normalizer.Normalize(f.Bytes!, recipe, f.SourceId, token);
                if (result.Status is "ok" or "passthrough")
                    await store.PutAsync(result.Record.Key, result.Output!, result.Record, token);
                EmitRecord(result.Record);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                EmitRecord(ResultRecord.Failed(f.Key, f.SourceId, recipe, e.Message));
            }
        });
        await closer;
        return counts;
    }

    /// <summary>
    /// Never throws for anything but cancellation: an item that cannot be fetched comes back as
    /// a <see cref="Fetched"/> carrying its error, so the fetch workers stay alive and every
    /// input item still reaches the process stage and gets its one manifest line.
    /// </summary>
    private static async Task<Fetched> FetchOneAsync(Item item, Recipe recipe, IOutputStore store, ISourceFetcher fetcher, bool refresh, CancellationToken ct)
    {
        var display = item.Path ?? item.Url!;
        try
        {
            if (item.Path is not null)
            {
                var bytes = await File.ReadAllBytesAsync(item.Path, ct);
                var id = SourceId.FromBytes(bytes);
                var key = OutputKey.Compute(id, recipe);
                if (!refresh && await store.ExistsAsync(key, ct)) return new Fetched(item, id, key, null, "skipped", null);
                return new Fetched(item, id, key, bytes, null, null);
            }

            string sourceId;
            try { sourceId = SourceId.FromUrl(item.Url!); }
            catch (PlinthException e) { return new Fetched(item, item.Url!, OutputKey.Compute(item.Url!, recipe), null, null, e.Message); }
            var urlKey = OutputKey.Compute(sourceId, recipe);
            if (!refresh && await store.ExistsAsync(urlKey, ct)) return new Fetched(item, sourceId, urlKey, null, "skipped", null);

            byte[] body;
            try { body = (await fetcher.FetchAsync(item.Url!, ct)).Bytes; }
            catch (PlinthException e) { return new Fetched(item, sourceId, urlKey, null, null, e.Message); }

            if (refresh)
            {
                var stored = await store.TryGetAsync(urlKey, ct);
                if (stored is not null && stored.Record.Source.Sha256 == Convert.ToHexStringLower(SHA256.HashData(body)))
                    return new Fetched(item, sourceId, urlKey, null, "unchanged", null);
            }
            return new Fetched(item, sourceId, urlKey, body, null, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new Fetched(item, display, OutputKey.Compute(display, recipe), null, null, e.Message);
        }
    }
}
