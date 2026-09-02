using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plinth.Core;

public sealed record SourceRecord(string Sha256, int Bytes, int Width, int Height, string Format, bool HadAlpha, int OrientationApplied);
public sealed record GroundRecord(string Sampled, int CornerSpread, bool CornersAgree, bool MatchesBackground);
public sealed record TrimRecord(int Left, int Top, int Width, int Height, bool Noop, double ContentShareBefore);
public sealed record VerdictRecord(bool PackShot, double Confidence, IReadOnlyList<string> Reasons);
public sealed record OutputRecord(int Width, int Height, int Bytes, string Format);
public sealed record TimingsRecord(long Inspect, long Measure, long Decode, long Render, long Encode, long Total)
{
    public static TimingsRecord Zero { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Everything Plinth learned about one image. Stored beside the output.
/// <c>LibvipsVersion</c> records which libvips produced it; unlike
/// <c>EngineVersion</c> it is not part of the key, so a mismatch is visible
/// without invalidating anything.
/// </summary>
public sealed record ResultRecord(
    string Key,
    string SourceId,
    string EngineVersion,
    string LibvipsVersion,
    string RecipeHash,
    string Status,
    string? Error,
    SourceRecord Source,
    GroundRecord Ground,
    TrimRecord Trim,
    VerdictRecord Verdict,
    OutputRecord? Output,
    TimingsRecord TimingsMs)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ResultRecord FromJson(string json) =>
        JsonSerializer.Deserialize<ResultRecord>(json, Options)
        ?? throw new PlinthException("record JSON was null");

    public static SourceRecord EmptySource { get; } = new("", 0, 0, 0, "", false, 1);
    public static GroundRecord EmptyGround { get; } = new("#000000", 0, false, false);
    public static TrimRecord EmptyTrim { get; } = new(0, 0, 0, 0, true, 0);
    public static VerdictRecord EmptyVerdict { get; } = new(false, 0, []);

    /// <summary>A record for work that never reached the pixels (fetch failed, bad URL).</summary>
    public static ResultRecord Failed(string key, string sourceId, Recipe recipe, string error) =>
        new(key, sourceId, Engine.Version, Engine.LibvipsVersion, recipe.Hash, "failed", error,
            EmptySource, EmptyGround, EmptyTrim, EmptyVerdict, null, TimingsRecord.Zero);
}
