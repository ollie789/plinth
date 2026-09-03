using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Plinth.Core;

/// <summary>The choices that define an output. Serialises canonically; hashes stably.</summary>
public sealed record Recipe
{
    public string Aspect { get; init; } = "4:5";
    public int Width { get; init; } = 1000;
    public double ContentShare { get; init; } = 0.85;
    public Rgb Background { get; init; } = Rgb.Parse("#ffffff");
    public int TrimThreshold { get; init; } = 12;
    public string Format { get; init; } = "webp";
    public int Quality { get; init; } = 84;
    public bool Upscale { get; init; } = true;

    /// <summary>
    /// What to do with an image the verdict says is not a pack shot:
    /// <c>passthrough</c> returns the source untouched, <c>card</c> trims and
    /// cards it like any other image.
    /// </summary>
    public string Editorial { get; init; } = "passthrough";

    public static Recipe Default { get; } = new Recipe().Validated();

    public int CanvasWidth => Width;
    public int CanvasHeight
    {
        get
        {
            var (w, h) = AspectParts();
            return (int)Math.Round(Width * (double)h / w);
        }
    }
    public int ContentBoxWidth => (int)Math.Round(CanvasWidth * ContentShare);
    public int ContentBoxHeight => (int)Math.Round(CanvasHeight * ContentShare);

    /// <summary>Sorted keys, no whitespace, invariant numbers. The hash input.</summary>
    public string Canonical()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(160);
        sb.Append("{\"aspect\":\"").Append(Aspect).Append('"');
        sb.Append(",\"background\":\"").Append(Background.ToHex()).Append('"');
        sb.Append(",\"contentShare\":").Append(ContentShare.ToString("0.0###", inv));
        sb.Append(",\"editorial\":\"").Append(Editorial).Append('"');
        sb.Append(",\"format\":\"").Append(Format).Append('"');
        sb.Append(",\"quality\":").Append(Quality.ToString(inv));
        sb.Append(",\"trimThreshold\":").Append(TrimThreshold.ToString(inv));
        sb.Append(",\"upscale\":").Append(Upscale ? "true" : "false");
        sb.Append(",\"width\":").Append(Width.ToString(inv));
        sb.Append('}');
        return sb.ToString();
    }

    public string Hash =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical())))[..16];

    public static Recipe FromJson(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new PlinthException($"recipe JSON is malformed: {ex.Message}", ex);
        }

        using (doc)
        {
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object)
                throw new PlinthException("recipe JSON is not an object");

            var r = new Recipe();
            foreach (var p in o.EnumerateObject())
            {
                try
                {
                    r = p.Name switch
                    {
                        "aspect" => r with { Aspect = p.Value.GetString() ?? "" },
                        "width" => r with { Width = p.Value.GetInt32() },
                        "contentShare" => r with { ContentShare = p.Value.GetDouble() },
                        "background" => r with { Background = Rgb.Parse(p.Value.GetString() ?? "") },
                        "trimThreshold" => r with { TrimThreshold = p.Value.GetInt32() },
                        "format" => r with { Format = p.Value.GetString() ?? "" },
                        "quality" => r with { Quality = p.Value.GetInt32() },
                        "upscale" => r with { Upscale = p.Value.GetBoolean() },
                        "editorial" => r with { Editorial = p.Value.GetString() ?? "" },
                        _ => throw new PlinthException($"unknown recipe field '{p.Name}'"),
                    };
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    throw new PlinthException($"recipe field '{p.Name}' has an invalid value", ex);
                }
            }
            return r.Validated();
        }
    }

    /// <summary>
    /// Throws on any field the pipeline cannot honour, and returns a copy whose
    /// contentShare is rounded to the four decimals <see cref="Canonical"/> emits,
    /// so the hash and the content-box arithmetic are computed from the same number.
    /// </summary>
    public Recipe Validated()
    {
        AspectParts();
        var share = Math.Round(ContentShare, 4);
        if (Width is < 16 or > 8000) throw new PlinthException("width must be 16..8000");
        if (share is <= 0 or > 1) throw new PlinthException("contentShare must be in (0, 1]");
        if (TrimThreshold is < 0 or > 255) throw new PlinthException("trimThreshold must be 0..255");
        if (Format is not ("webp" or "png")) throw new PlinthException("format must be webp or png");
        if (Quality is < 1 or > 100) throw new PlinthException("quality must be 1..100");
        if (Editorial is not ("passthrough" or "card")) throw new PlinthException("editorial must be passthrough or card");
        return share.Equals(ContentShare) ? this : this with { ContentShare = share };
    }

    private (int w, int h) AspectParts()
    {
        var parts = Aspect.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            && w > 0 && h > 0)
            return (w, h);
        throw new PlinthException($"aspect must be w:h, got '{Aspect}'");
    }
}
