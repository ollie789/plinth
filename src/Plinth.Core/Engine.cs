namespace Plinth.Core;

/// <summary>Algorithm identity and one-time libvips configuration.</summary>
public static class Engine
{
    /// <summary>
    /// Version of the normalisation algorithm, not of the package. It is part
    /// of every output key; bump it only when output bytes would change.
    /// </summary>
    public const string Version = "1.0";

    private static readonly object Gate = new();
    private static bool _initialised;

    public static string LibvipsVersion =>
        $"{NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}";

    /// <summary>Configure libvips once. Safe to call repeatedly.</summary>
    public static void Init()
    {
        lock (Gate)
        {
            if (_initialised) return;
            // The operation cache only helps when the same operation is
            // repeated on the same image. Each image here is seen once.
            NetVips.Cache.Max = 0;
            NetVips.NetVips.Concurrency = Environment.ProcessorCount;
            _initialised = true;
        }
    }
}
