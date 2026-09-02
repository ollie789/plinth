using System.Globalization;

namespace Plinth.Core;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Parse(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new PlinthException($"colour must be #rrggbb, got '{hex}'");
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>Largest per-channel difference. Matches libvips' trim threshold semantics.</summary>
    public int Distance(Rgb o) =>
        Math.Max(Math.Abs(R - o.R), Math.Max(Math.Abs(G - o.G), Math.Abs(B - o.B)));

    public double[] ToVips() => [R, G, B];

    public static Rgb FromDoubles(double r, double g, double b) =>
        new((byte)Math.Clamp(Math.Round(r), 0, 255),
            (byte)Math.Clamp(Math.Round(g), 0, 255),
            (byte)Math.Clamp(Math.Round(b), 0, 255));
}
