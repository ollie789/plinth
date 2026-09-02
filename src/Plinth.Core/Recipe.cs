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
    public double ContentShare { get; init; } = 0.78;
    public Rgb Background { get; init; } = Rgb.Parse("#ffffff");
    public int TrimThreshold { get; init; } = 12;
    public string Format { get; init; } = "webp";
    public int Quality { get; init; } = 84;
    public bool Upscale { get; init; } = true;

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
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        var r = new Recipe();
        foreach (var p in o.EnumerateObject())
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
                _ => throw new PlinthException($"unknown recipe field '{p.Name}'"),
            };
        }
        return r.Validated();
    }

    public Recipe Validated()
    {
        AspectParts();
        if (Width is < 16 or > 8000) throw new PlinthException("width must be 16..8000");
        if (ContentShare is <= 0 or > 1) throw new PlinthException("contentShare must be in (0, 1]");
        if (TrimThreshold is < 0 or > 255) throw new PlinthException("trimThreshold must be 0..255");
        if (Format is not ("webp" or "png")) throw new PlinthException("format must be webp or png");
        if (Quality is < 1 or > 100) throw new PlinthException("quality must be 1..100");
        return this;
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
