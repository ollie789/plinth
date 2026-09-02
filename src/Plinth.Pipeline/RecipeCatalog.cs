using System.Text.Json;
using Plinth.Core;

namespace Plinth.Pipeline;

/// <summary>Named recipes. "default" is always present and always Recipe.Default unless overridden.</summary>
public sealed class RecipeCatalog
{
    private readonly SortedDictionary<string, Recipe> _recipes;

    private RecipeCatalog(SortedDictionary<string, Recipe> recipes) => _recipes = recipes;

    public static RecipeCatalog DefaultOnly { get; } = new(new SortedDictionary<string, Recipe>(StringComparer.Ordinal) { ["default"] = Recipe.Default });

    public static RecipeCatalog FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new PlinthException("recipes JSON must be an object of name → recipe");
        var map = new SortedDictionary<string, Recipe>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
            map[p.Name] = Recipe.FromJson(p.Value.GetRawText());
        map.TryAdd("default", Recipe.Default);
        return new RecipeCatalog(map);
    }

    public IReadOnlyList<string> Names => _recipes.Keys.ToList();

    public Recipe Get(string? name)
    {
        var n = string.IsNullOrEmpty(name) ? "default" : name;
        return _recipes.TryGetValue(n, out var r) ? r : throw new PlinthException($"unknown recipe '{n}'");
    }
}
