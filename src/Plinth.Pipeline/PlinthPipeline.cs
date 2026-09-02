using Plinth.Core;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;

namespace Plinth.Pipeline;

public sealed record PipelineResult(string Status, byte[]? Bytes, ResultRecord Record, bool FromStore);

/// <summary>Check the store by key, fetch, normalise, store. The one flow both front doors share.</summary>
public sealed class PlinthPipeline(ISourceFetcher fetcher, IOutputStore store, RecipeCatalog recipes)
{
    public RecipeCatalog Recipes { get; } = recipes;

    public async Task<PipelineResult> ProcessUrlAsync(string url, string? recipeName, CancellationToken ct = default)
    {
        Recipe recipe;
        try { recipe = Recipes.Get(recipeName); }
        catch (PlinthException e) { return UnknownRecipeResult(url, recipeName, e.Message); }

        string sourceId;
        try { sourceId = SourceId.FromUrl(url); }
        catch (PlinthException e) { return Failed(url, url, recipe, e.Message); }

        var key = OutputKey.Compute(sourceId, recipe);
        var cached = await store.TryGetAsync(key, ct);
        if (cached is not null) return new PipelineResult(cached.Record.Status, cached.Bytes, cached.Record, FromStore: true);

        byte[] bytes;
        try { bytes = (await fetcher.FetchAsync(url, ct)).Bytes; }
        catch (PlinthException e) { return new PipelineResult("failed", null, ResultRecord.Failed(key, sourceId, recipe, e.Message), false); }

        return await NormalizeAndStoreAsync(bytes, recipe, sourceId, ct);
    }

    public async Task<PipelineResult> ProcessBytesAsync(byte[] bytes, string? recipeName, string? sourceId, CancellationToken ct = default)
    {
        var id = sourceId ?? SourceId.FromBytes(bytes);

        Recipe recipe;
        try { recipe = Recipes.Get(recipeName); }
        catch (PlinthException e) { return UnknownRecipeResult(id, recipeName, e.Message); }

        var key = OutputKey.Compute(id, recipe);
        var cached = await store.TryGetAsync(key, ct);
        if (cached is not null) return new PipelineResult(cached.Record.Status, cached.Bytes, cached.Record, FromStore: true);
        return await NormalizeAndStoreAsync(bytes, recipe, id, ct);
    }

    private async Task<PipelineResult> NormalizeAndStoreAsync(byte[] bytes, Recipe recipe, string sourceId, CancellationToken ct)
    {
        var result = Normalizer.Normalize(bytes, recipe, sourceId, ct);
        if (result.Status is "ok" or "passthrough")
            await store.PutAsync(result.Record.Key, result.Output!, result.Record, ct);
        return new PipelineResult(result.Status, result.Output, result.Record, FromStore: false);
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
