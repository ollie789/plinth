using NetVips;

namespace Plinth.Core;

/// <summary>
/// 64-bit difference hash: shrink to 9x8 greyscale, set a bit where each
/// pixel is brighter than its right neighbour. Robust to re-encoding and
/// small resizes; used for golden tests now and duplicate detection later.
/// </summary>
public static class PerceptualHash
{
    public static ulong DHash(byte[] imageBytes)
    {
        Engine.Init();
        using var img = Image.ThumbnailBuffer(imageBytes, 9, height: 8, size: Enums.Size.Force);
        using var grey = img.Colourspace(Enums.Interpretation.Bw);
        using var flat = grey.HasAlpha() ? grey.Flatten(background: [255]) : grey.Copy();
        ulong hash = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var left = flat.Getpoint(x, y)[0];
                var right = flat.Getpoint(x + 1, y)[0];
                hash = (hash << 1) | (left > right ? 1UL : 0UL);
            }
        return hash;
    }

    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    public static string ToHex(ulong h) => h.ToString("x16");
}
