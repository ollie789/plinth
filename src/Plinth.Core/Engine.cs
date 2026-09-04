namespace Plinth.Core;

/// <summary>Algorithm identity and one-time libvips configuration.</summary>
public static class Engine
{
    /// <summary>
    /// Version of the normalisation algorithm, not of the package. It is part
    /// of every output key; bump it only when output bytes would change.
    /// </summary>
    public const string Version = "1.5";

    /// <summary>Environment override for the worker-thread count, read once at first Init.</summary>
    public const string ConcurrencyVariable = "PLINTH_CONCURRENCY";

    private static readonly object Gate = new();
    private static bool _initialised;

    public static string LibvipsVersion =>
        $"{NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}";

    /// <summary>Worker threads libvips uses per image, as actually applied.</summary>
    public static int Concurrency { get; private set; }

    /// <summary>
    /// Configure libvips once. Safe to call repeatedly. An explicit
    /// <paramref name="concurrency"/> is applied every time, because the worker
    /// count is safe to change between operations; null means "leave it as it is"
    /// once initialised. Unset, it comes from <see cref="ConcurrencyVariable"/>,
    /// else from the processor count.
    /// </summary>
    public static void Init(int? concurrency = null)
    {
        lock (Gate)
        {
            if (_initialised)
            {
                if (concurrency is { } later) Apply(later);
                return;
            }
            // The operation cache only helps when the same operation is
            // repeated on the same image. Each image here is seen once.
            NetVips.Cache.Max = 0;
            Apply(concurrency ?? FromEnvironment() ?? Environment.ProcessorCount);
            _initialised = true;
        }
    }

    private static void Apply(int concurrency)
    {
        NetVips.NetVips.Concurrency = concurrency;
        // Read back: libvips clamps what it is given, so this is the applied value.
        Concurrency = NetVips.NetVips.Concurrency;
    }

    /// <summary>
    /// Run one tiny normalise so the first real request does not pay for JIT and for libvips
    /// loading its decoders and encoders. The image is drawn in memory — no fixture on disk,
    /// nothing to ship — and the result is thrown away. Batch runs skip this: they amortise
    /// the same cost over the first few of many images anyway.
    /// </summary>
    public static void WarmUp()
    {
        Init();
        using var ground = NetVips.Image.Black(64, 80, bands: 3) + new double[] { 255, 255, 255 };
        using var box = NetVips.Image.Black(24, 30, bands: 3);
        using var image = ground.Insert(box, 20, 25)
            .Cast(NetVips.Enums.BandFormat.Uchar)
            .Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
        _ = Normalizer.Normalize(image.JpegsaveBuffer(q: 90), Recipe.Default, "plinth:warmup");
    }

    private static int? FromEnvironment() =>
        int.TryParse(Environment.GetEnvironmentVariable(ConcurrencyVariable),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : null;
}
