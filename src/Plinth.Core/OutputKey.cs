using System.Security.Cryptography;
using System.Text;

namespace Plinth.Core;

/// <summary>key = sha256(sourceId | recipeHash | engineVersion), lowercase hex.</summary>
public static class OutputKey
{
    public static string Compute(string sourceId, Recipe recipe) =>
        Compute(sourceId, recipe.Hash, Engine.Version);

    public static string Compute(string sourceId, string recipeHash, string engineVersion) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{sourceId}|{recipeHash}|{engineVersion}")));
}
