namespace Plinth.Tests;

public static class Fixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures");
    public static string SrcDir => Path.Combine(Dir, "src");
    public static string GoldenDir => Path.Combine(Dir, "golden");

    public static IEnumerable<(string Name, byte[] Bytes)> All() =>
        Directory.EnumerateFiles(SrcDir)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)));

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(SrcDir, name));
}
