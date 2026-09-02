using Plinth.Core;
using Plinth.Pipeline.Fetch;

namespace Plinth.Pipeline;

/// <summary>Everything the front doors read from the environment (spec 6.1).</summary>
public sealed record PipelineOptions(
    FetchPolicy Fetch,
    string StoreUri,
    RecipeCatalog Recipes,
    string? SigningKey,
    string OnFailure,
    int? Concurrency)
{
    public static PipelineOptions FromEnvironment(Func<string, string?> env)
    {
        var onFailure = env("PLINTH_ON_FAILURE") ?? "redirect";
        if (onFailure is not ("redirect" or "error")) throw new PlinthException("PLINTH_ON_FAILURE must be redirect or error");
        var recipesPath = env("PLINTH_RECIPES");
        var recipes = string.IsNullOrEmpty(recipesPath) ? RecipeCatalog.DefaultOnly : RecipeCatalog.FromJson(File.ReadAllText(recipesPath));
        int? concurrency = int.TryParse(env("PLINTH_CONCURRENCY"), out var c) && c > 0 ? c : null;
        var signing = env("PLINTH_SIGNING_KEY");
        return new PipelineOptions(
            FetchPolicy.FromHostList(env("PLINTH_ALLOWED_HOSTS")),
            env("PLINTH_STORE") ?? "none",
            recipes,
            string.IsNullOrEmpty(signing) ? null : signing,
            onFailure,
            concurrency);
    }
}
