using Plinth.Core;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;

namespace Plinth.Pipeline;

/// <summary>
/// <c>StoreFault</c> is set when the store threw on the way: the read was treated as a miss,
/// or the write was skipped, and the image was produced anyway. The front door logs it.
/// </summary>
public sealed record PipelineResult(string Status, byte[]? Bytes, ResultRecord Record, bool FromStore, string? StoreFault = null);

/// <summary>Check the store by key, fetch, normalise, store. The one flow both front doors share.</summary>
public sealed class PlinthPipeline(ISourceFetcher fetcher, IOutputStore store, RecipeCatalog recipes)
{
    public RecipeCatalog Recipes { get; } = recipes;

    public Task<PipelineResult> ProcessUrlAsync(string url, string? recipeName, CancellationToken ct = default) =>
        FromUrlAsync(url, recipeName, recordOnly: false, ct);

    /// <summary>
    /// Same flow as <see cref="ProcessUrlAsync"/>, except a store hit is satisfied from the
    /// record alone: a caller that only wants the JSON should never make the store move the
    /// image bytes. A hit comes back with <c>FromStore: true</c> and no <c>Bytes</c>.
    /// </summary>
    public Task<PipelineResult> InspectUrlAsync(string url, string? recipeName, CancellationToken ct = default) =>
        FromUrlAsync(url, recipeName, recordOnly: true, ct);

    private async Task<PipelineResult> FromUrlAsync(string url, string? recipeName, bool recordOnly, CancellationToken ct)
    {
        // Before anything else, so the upgraded URL is what gets fetched, what
        // the record calls its source and what the key is computed from: a
        // thumbnail and its master are different sources, not two spellings of
        // one, and must never share a tile.
        url = SourceUpgrades.Apply(url);

        Recipe recipe;
        try { recipe = Recipes.Get(recipeName); }
        catch (PlinthException e) { return UnknownRecipeResult(url, recipeName, e.Message); }

        string sourceId;
        try { sourceId = SourceId.FromUrl(url); }
        catch (PlinthException e) { return Failed(url, url, recipe, e.Message); }

        var key = OutputKey.Compute(sourceId, recipe);
        string? fault;
        if (recordOnly)
        {
            var (record, f) = await Guarded(() => store.TryGetRecordAsync(key, ct));
            if (record is not null) return new PipelineResult(record.Status, null, record, FromStore: true);
            fault = f;
        }
        else
        {
            var (cached, f) = await Guarded(() => store.TryGetAsync(key, ct));
            if (cached is not null) return new PipelineResult(cached.Record.Status, cached.Bytes, cached.Record, FromStore: true);
            fault = f;
        }

        byte[] bytes;
        try { bytes = (await fetcher.FetchAsync(url, ct)).Bytes; }
        catch (PlinthException e) { return new PipelineResult("failed", null, ResultRecord.Failed(key, sourceId, recipe, e.Message), false, fault); }

        return await NormalizeAndStoreAsync(bytes, recipe, sourceId, ct, fault);
    }

    public async Task<PipelineResult> ProcessBytesAsync(byte[] bytes, string? recipeName, string? sourceId, CancellationToken ct = default)
    {
        var id = sourceId ?? SourceId.FromBytes(bytes);

        Recipe recipe;
        try { recipe = Recipes.Get(recipeName); }
        catch (PlinthException e) { return UnknownRecipeResult(id, recipeName, e.Message); }

        var key = OutputKey.Compute(id, recipe);
        var (cached, fault) = await Guarded(() => store.TryGetAsync(key, ct));
        if (cached is not null) return new PipelineResult(cached.Record.Status, cached.Bytes, cached.Record, FromStore: true);
        return await NormalizeAndStoreAsync(bytes, recipe, id, ct, fault);
    }

    private async Task<PipelineResult> NormalizeAndStoreAsync(byte[] bytes, Recipe recipe, string sourceId, CancellationToken ct, string? fault)
    {
        var result = Normalizer.Normalize(bytes, recipe, sourceId, ct);
        if (result.Status is "ok" or "passthrough")
        {
            try { await store.PutAsync(result.Record.Key, result.Output!, result.Record, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { fault = fault is null ? Describe(e) : $"{fault}; {Describe(e)}"; }
        }
        return new PipelineResult(result.Status, result.Output, result.Record, FromStore: false, fault);
    }

    /// <summary>
    /// The store is a cache, not a dependency. A read that throws is a miss; a write that
    /// throws is a result nobody kept. Either way the caller gets its image, and the fault
    /// rides on the result for the front door to log and count. Cancellation is the caller's
    /// own and passes through untouched.
    /// </summary>
    private static async Task<(T? Value, string? Fault)> Guarded<T>(Func<Task<T?>> read) where T : class
    {
        try { return (await read(), null); }
        catch (Exception e) when (e is not OperationCanceledException) { return (null, Describe(e)); }
    }

    /// <summary>Type and first line only: enough to triage, and never a stack.</summary>
    private static string Describe(Exception e)
    {
        var line = e.Message.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? e.GetType().Name : $"{e.GetType().Name}: {line}";
    }

    private static PipelineResult Failed(string sourceId, string keySource, Recipe recipe, string error) =>
        new("failed", null, ResultRecord.Failed(OutputKey.Compute(keySource, recipe), sourceId, recipe, error), false);

    /// <summary>
    /// An unknown recipe name never had a real recipe to hash, so the key cannot be
    /// computed with <see cref="Recipe.Hash"/>. Salting with the (unhashed) recipe name
    /// under a namespace that is not 16 hex characters guarantees this key can never
    /// collide with a stored artefact's key for the same source.
    /// </summary>
    private static PipelineResult UnknownRecipeResult(string sourceId, string? name, string error)
    {
        var key = OutputKey.Compute(sourceId, "unknown-recipe:" + name, Engine.Version);
        return new PipelineResult("failed", null, ResultRecord.Failed(key, sourceId, Recipe.Default, error), false);
    }
}
