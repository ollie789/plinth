# Plinth Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Plinth.Core`, the pure, deterministic pack-shot normaliser (recipe, key, measure, trim, verdict, canvas, encode, result record) with golden-fixture tests and a benchmark harness, as a .NET 10 class library.

**Architecture:** One class library with no knowledge of HTTP, files or Azure. A `Normalizer` orchestrates four small units: `SourceInspector` (header facts and caps), `Measurer` (ground sampling and trim box on a small decode), `VerdictScorer` (pack-shot heuristic, reported only), and `Renderer` (shrink-on-load decode, crop, fit, canvas, encode). Every call returns a `NormalizeResult` carrying bytes and a `ResultRecord`. The API and CLI are a later plan and only ever call `Normalizer.Normalize`.

**Tech Stack:** .NET 10 SDK (10.0.100 installed at `~/.local/bin/dotnet`), C# 14, NetVips 3.2.0 over libvips 8.18.6 (`NetVips.Native.osx-arm64` for this Mac, `NetVips.Native.linux-x64` for CI and Docker), xUnit, System.Text.Json, Node 20+ only for the one-off fixture fetch script.

**Spec:** `docs/2026-09-02-plinth-design.md` (sections 4, 5, 11, 12 are the ones this plan implements; 6 to 9 are the next plan).

## Global Constraints

- Repository root is `/Users/oliver/Projects/plinth`; all paths below are relative to it. Run every command from there.
- `dotnet` is at `/Users/oliver/.local/bin/dotnet`; make sure it is on `PATH` (`export PATH="$HOME/.local/bin:$PATH"`).
- Target framework `net10.0` for every project. `Nullable` enabled, `ImplicitUsings` enabled, `TreatWarningsAsErrors` true.
- Package versions are pinned in `Directory.Packages.props`: `NetVips` 3.2.0, `NetVips.Native.osx-arm64` 8.18.6, `NetVips.Native.linux-x64` 8.18.6. Never float them.
- Recipe defaults are the spec's, verbatim: aspect `4:5`, width `1000`, contentShare `0.78`, background `#ffffff`, trimThreshold `12`, format `webp`, quality `84`, upscale `true`.
- Algorithm version constant `Engine.Version = "1.0"`. It is part of every key; bump only when output would change.
- Caps: `MaxPixels = 30_000_000`. (Byte caps and timeouts belong to the fetchers in the next plan; Core takes bytes.)
- Core must be pure: no network, no clock in outputs (timings are measured but never affect bytes or keys), no randomness, no file I/O except in tests and the bench.
- Every task ends with all tests green (`dotnet test`) and a commit. Commit messages are one line, present tense, no prefixes.
- Git identity for commits: `Oliver Bingemann <ollie.bingemann@gmail.com>`.

---

## File structure

```
Plinth.sln
Directory.Build.props                  shared build settings
Directory.Packages.props               central package versions
LICENSE                                MIT
README.md
.gitignore
src/Plinth.Core/
  Plinth.Core.csproj
  Engine.cs                            Version constant, libvips init
  Recipe.cs                            Recipe record, parsing, canonical JSON, hash
  SourceId.cs                          canonical URL / sha256 source identity
  OutputKey.cs                         key = sha256(sourceId|recipeHash|version)
  PlinthException.cs                   the one exception type Core throws
  Rgb.cs                               small colour struct + hex parsing + distance
  SourceInspector.cs                   header facts, pixel cap
  Measurer.cs                          ground sampling, trim box, content share
  VerdictScorer.cs                     pack-shot heuristic
  Renderer.cs                          decode at need, crop, fit, canvas, encode
  PerceptualHash.cs                    dHash for golden tests and future dedupe
  ResultRecord.cs                      the record types + JSON
  Normalizer.cs                        orchestration, passthrough, failure posture
src/Plinth.Bench/
  Plinth.Bench.csproj
  Program.cs                           fixture benchmark, baseline compare
tests/Plinth.Tests/
  Plinth.Tests.csproj
  Synthetic.cs                         helpers that draw test images with NetVips
  Fixtures.cs                          enumerates tests/fixtures/src
  RecipeTests.cs
  SourceIdTests.cs
  OutputKeyTests.cs
  SourceInspectorTests.cs
  MeasurerTests.cs
  VerdictScorerTests.cs
  RendererTests.cs
  NormalizerTests.cs
  GoldenTests.cs
  fixtures/src/<host>.<ext>            one real pack shot per engine host
  fixtures/sources.txt                 the URLs those came from
  fixtures/golden/<host>.json          expected record subset + dhash
tools/fetch-fixtures.mjs               one-off: pull one image per host from the feed
docs/bench/baseline.json               committed benchmark baseline
```

---

### Task 1: Repository scaffold

**Files:**
- Create: `Plinth.sln`, `Directory.Build.props`, `Directory.Packages.props`, `LICENSE`, `README.md`, `.gitignore`
- Create: `src/Plinth.Core/Plinth.Core.csproj`, `src/Plinth.Core/Engine.cs`
- Create: `tests/Plinth.Tests/Plinth.Tests.csproj`, `tests/Plinth.Tests/EngineTests.cs`

**Interfaces:**
- Produces: `Plinth.Core.Engine.Version` (string), `Plinth.Core.Engine.Init()` (idempotent libvips setup).

- [ ] **Step 1: Create the solution and projects**

```bash
cd /Users/oliver/Projects/plinth
export PATH="$HOME/.local/bin:$PATH"
dotnet new sln -n Plinth
dotnet new classlib -n Plinth.Core -o src/Plinth.Core -f net10.0
dotnet new xunit -n Plinth.Tests -o tests/Plinth.Tests -f net10.0
rm src/Plinth.Core/Class1.cs tests/Plinth.Tests/UnitTest1.cs
dotnet sln add src/Plinth.Core/Plinth.Core.csproj tests/Plinth.Tests/Plinth.Tests.csproj
dotnet add tests/Plinth.Tests/Plinth.Tests.csproj reference src/Plinth.Core/Plinth.Core.csproj
```

- [ ] **Step 2: Write the shared build files**

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`:

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="NetVips" Version="3.2.0" />
    <PackageVersion Include="NetVips.Native.osx-arm64" Version="8.18.6" />
    <PackageVersion Include="NetVips.Native.linux-x64" Version="8.18.6" />
  </ItemGroup>
</Project>
```

The xunit template already wrote its own `PackageReference` lines with versions into the test csproj. Central versioning requires them to live in `Directory.Packages.props`, so move them: open `tests/Plinth.Tests/Plinth.Tests.csproj`, copy each `<PackageReference Include="X" Version="Y" />` into `Directory.Packages.props` as `<PackageVersion Include="X" Version="Y" />`, then delete the `Version` attribute from each reference in the csproj.

`src/Plinth.Core/Plinth.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Plinth.Core</RootNamespace>
    <AssemblyName>Plinth.Core</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NetVips" />
  </ItemGroup>
</Project>
```

Add to `tests/Plinth.Tests/Plinth.Tests.csproj` inside its `<ItemGroup>` with package references (the natives are runtime packages; both can coexist and the loader picks the one for the current OS):

```xml
    <PackageReference Include="NetVips.Native.osx-arm64" />
    <PackageReference Include="NetVips.Native.linux-x64" />
```

`.gitignore`:

```
bin/
obj/
*.user
.vs/
.idea/
.DS_Store
TestResults/
docs/bench/latest.json
```

`LICENSE`: the MIT licence text with `Copyright (c) 2026 Oliver Bingemann`.

`README.md`:

```markdown
# Plinth

Puts every product on the same plinth. Trims retailer pack shots to their
content and re-centres them on one canvas so a grid of products from many
sellers reads as one catalogue.

Design: `docs/2026-09-02-plinth-design.md`. Build plan: `docs/plans/`.

Requires the .NET 10 SDK. `dotnet test` runs everything.
```

- [ ] **Step 3: Write the failing test for the engine constant**

`tests/Plinth.Tests/EngineTests.cs`:

```csharp
using Plinth.Core;

namespace Plinth.Tests;

public class EngineTests
{
    [Fact]
    public void Version_is_the_algorithm_version()
    {
        Assert.Equal("1.0", Engine.Version);
    }

    [Fact]
    public void Init_is_idempotent_and_reports_libvips()
    {
        Engine.Init();
        Engine.Init();
        Assert.StartsWith("8.", Engine.LibvipsVersion);
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test`
Expected: build error, `Engine` does not exist.

- [ ] **Step 5: Write Engine.cs**

`src/Plinth.Core/Engine.cs`:

```csharp
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
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test`
Expected: 2 passed.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Scaffold the solution, core library and test project"
```

---

### Task 2: Recipe, canonical JSON and recipe hash

**Files:**
- Create: `src/Plinth.Core/Rgb.cs`, `src/Plinth.Core/PlinthException.cs`, `src/Plinth.Core/Recipe.cs`
- Test: `tests/Plinth.Tests/RecipeTests.cs`

**Interfaces:**
- Produces: `Rgb` (readonly record struct with `R,G,B` bytes, `Parse(string hex)`, `ToHex()`, `Distance(Rgb)` Chebyshev, `ToVips()` → `double[]`), `PlinthException`, `Recipe` (record with the eight fields), `Recipe.Default`, `Recipe.Canonical()` (string), `Recipe.Hash` (16 lowercase hex), `Recipe.CanvasWidth/CanvasHeight/ContentBoxWidth/ContentBoxHeight` (ints), `Recipe.FromJson(string)`.

- [ ] **Step 1: Write the failing tests**

`tests/Plinth.Tests/RecipeTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Plinth.Core;

namespace Plinth.Tests;

public class RecipeTests
{
    [Fact]
    public void Default_matches_the_spec()
    {
        var r = Recipe.Default;
        Assert.Equal("4:5", r.Aspect);
        Assert.Equal(1000, r.Width);
        Assert.Equal(0.78, r.ContentShare);
        Assert.Equal("#ffffff", r.Background.ToHex());
        Assert.Equal(12, r.TrimThreshold);
        Assert.Equal("webp", r.Format);
        Assert.Equal(84, r.Quality);
        Assert.True(r.Upscale);
    }

    [Fact]
    public void Canonical_json_has_sorted_keys_and_no_whitespace()
    {
        Assert.Equal(
            "{\"aspect\":\"4:5\",\"background\":\"#ffffff\",\"contentShare\":0.78,\"format\":\"webp\",\"quality\":84,\"trimThreshold\":12,\"upscale\":true,\"width\":1000}",
            Recipe.Default.Canonical());
    }

    [Fact]
    public void Hash_is_first_16_hex_of_sha256_of_canonical()
    {
        var expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(Recipe.Default.Canonical())))[..16];
        Assert.Equal(expected, Recipe.Default.Hash);
    }

    [Fact]
    public void Any_field_change_changes_the_hash()
    {
        var a = Recipe.Default;
        Assert.NotEqual(a.Hash, (a with { Quality = 80 }).Hash);
        Assert.NotEqual(a.Hash, (a with { Background = Rgb.Parse("#fafafa") }).Hash);
        Assert.Equal(a.Hash, (a with { Quality = 84 }).Hash);
    }

    [Fact]
    public void Canvas_and_content_box_derive_from_aspect_width_and_share()
    {
        var r = Recipe.Default;
        Assert.Equal(1000, r.CanvasWidth);
        Assert.Equal(1250, r.CanvasHeight);
        Assert.Equal(780, r.ContentBoxWidth);
        Assert.Equal(975, r.ContentBoxHeight);
        var sq = r with { Aspect = "1:1", Width = 800 };
        Assert.Equal(800, sq.CanvasHeight);
    }

    [Fact]
    public void FromJson_accepts_partial_objects_and_rejects_bad_values()
    {
        var r = Recipe.FromJson("{\"quality\":70,\"format\":\"png\"}");
        Assert.Equal(70, r.Quality);
        Assert.Equal("png", r.Format);
        Assert.Equal(1000, r.Width);
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{\"aspect\":\"wide\"}"));
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{\"format\":\"gif\"}"));
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{\"contentShare\":1.5}"));
    }

    [Fact]
    public void Rgb_parses_hex_and_measures_chebyshev_distance()
    {
        var a = Rgb.Parse("#ffffff");
        var b = Rgb.Parse("#f0ff00");
        Assert.Equal(255, a.Distance(b));
        Assert.Equal(new double[] { 255, 255, 255 }, a.ToVips());
        Assert.Throws<PlinthException>(() => Rgb.Parse("white"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build error, `Recipe`, `Rgb` and `PlinthException` do not exist.

- [ ] **Step 3: Write the implementation**

`src/Plinth.Core/PlinthException.cs`:

```csharp
namespace Plinth.Core;

/// <summary>The one exception type Core throws for bad input or bad images.</summary>
public sealed class PlinthException(string message, Exception? inner = null)
    : Exception(message, inner);
```

`src/Plinth.Core/Rgb.cs`:

```csharp
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
```

`src/Plinth.Core/Recipe.cs`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass (9 tests so far).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Recipe with canonical JSON and stable hash"
```

---

### Task 3: Source identity and output key

**Files:**
- Create: `src/Plinth.Core/SourceId.cs`, `src/Plinth.Core/OutputKey.cs`
- Test: `tests/Plinth.Tests/SourceIdTests.cs`, `tests/Plinth.Tests/OutputKeyTests.cs`

**Interfaces:**
- Produces: `SourceId.FromUrl(string url)` → string (canonical URL), `SourceId.FromBytes(ReadOnlySpan<byte>)` → `"sha256:<64 hex>"`, `OutputKey.Compute(string sourceId, Recipe recipe)` → 64 lowercase hex (uses `Engine.Version`), `OutputKey.Compute(string sourceId, string recipeHash, string engineVersion)`.

- [ ] **Step 1: Write the failing tests**

`tests/Plinth.Tests/SourceIdTests.cs`:

```csharp
using Plinth.Core;

namespace Plinth.Tests;

public class SourceIdTests
{
    [Fact]
    public void Url_is_canonicalised_but_query_is_preserved()
    {
        Assert.Equal(
            "https://img1.theiconic.com.au/a/b.jpg?v=3",
            SourceId.FromUrl("HTTPS://IMG1.TheIconic.com.au:443/a/b.jpg?v=3#frag"));
    }

    [Fact]
    public void Url_must_be_https_and_absolute()
    {
        Assert.Throws<PlinthException>(() => SourceId.FromUrl("http://x.com/a.jpg"));
        Assert.Throws<PlinthException>(() => SourceId.FromUrl("/a.jpg"));
        Assert.Throws<PlinthException>(() => SourceId.FromUrl("not a url"));
    }

    [Fact]
    public void Bytes_id_is_sha256_prefixed()
    {
        var id = SourceId.FromBytes("hello"u8);
        Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", id);
    }
}
```

`tests/Plinth.Tests/OutputKeyTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Plinth.Core;

namespace Plinth.Tests;

public class OutputKeyTests
{
    [Fact]
    public void Key_is_sha256_of_sourceId_recipeHash_and_version()
    {
        var src = "https://img1.theiconic.com.au/a.jpg";
        var expected = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{src}|{Recipe.Default.Hash}|{Engine.Version}")));
        Assert.Equal(expected, OutputKey.Compute(src, Recipe.Default));
        Assert.Equal(64, expected.Length);
    }

    [Fact]
    public void Key_changes_with_source_recipe_or_version()
    {
        var a = OutputKey.Compute("https://a/x.jpg", Recipe.Default);
        Assert.NotEqual(a, OutputKey.Compute("https://a/y.jpg", Recipe.Default));
        Assert.NotEqual(a, OutputKey.Compute("https://a/x.jpg", Recipe.Default with { Quality = 1 }));
        Assert.NotEqual(a, OutputKey.Compute("https://a/x.jpg", Recipe.Default.Hash, "9.9"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build error, `SourceId` and `OutputKey` do not exist.

- [ ] **Step 3: Write the implementation**

`src/Plinth.Core/SourceId.cs`:

```csharp
using System.Security.Cryptography;

namespace Plinth.Core;

/// <summary>
/// The identity of a source: its canonical URL when fetched, otherwise the
/// hash of its bytes. Chosen so a key can be computed from a URL without
/// downloading anything.
/// </summary>
public static class SourceId
{
    public static string FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new PlinthException($"source must be an absolute https URL, got '{url}'");
        var b = new UriBuilder(u) { Fragment = "", Scheme = "https" };
        b.Host = b.Host.ToLowerInvariant();
        if (b.Port == 443) b.Port = -1;
        return b.Uri.GetComponents(
            UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }

    public static string FromBytes(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
```

`src/Plinth.Core/OutputKey.cs`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Source identity and URL-addressable output key"
```

---

### Task 4: Golden fixtures from the live feed

**Files:**
- Create: `tools/fetch-fixtures.mjs`, `tests/fixtures/sources.txt`, `tests/fixtures/src/*` (downloaded), `tests/fixtures/README.md`
- Create: `tests/Plinth.Tests/Fixtures.cs`, `tests/Plinth.Tests/FixturesTests.cs`
- Modify: `tests/Plinth.Tests/Plinth.Tests.csproj` (copy fixtures to output)

**Interfaces:**
- Produces: `Fixtures.Dir` (absolute path), `Fixtures.All()` → `IEnumerable<(string Name, byte[] Bytes)>` sorted by name, `Fixtures.Read(string name)` → byte[].

The engine hosts are the fifteen in Mahir's `lib/image-engine.ts`. The CF feed (`FEED_BASE_URL`, bearer `FEED_TOKEN`, both in `/Users/oliver/Projects/Mahir/.env.local`) lists collections at `/v1/collections` and products at `/v1/collections/{slug}/products?page=N&per_page=100`; each product has `images[].src`. Nine of the fifteen hosts had samples at the time the engine shipped; get every host that has one today.

- [ ] **Step 1: Write the fetch script**

`tools/fetch-fixtures.mjs`:

```javascript
#!/usr/bin/env node
// One-off: pull one pack shot per engine host from the CF feed into
// tests/fixtures/src and record the URLs in tests/fixtures/sources.txt.
// usage: node tools/fetch-fixtures.mjs <feed-base-url> <bearer-token>
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";

const ENGINE_HOSTS = [
  "img1.theiconic.com.au", "www.platypusshoes.com.au", "images.puma.com",
  "assets.adidas.com", "www.citybeach.com", "contents.mediadecathlon.com",
  "m.media-amazon.com", "myer-media.com.au", "img.zcdn.com.au",
  "images.asos-media.com", "feeds.frgimages.com", "www.davidjones.com",
  "www.adairs.com.au", "i8.amplience.net", "media.bunnings.com.au",
];
const [base, token] = process.argv.slice(2);
if (!base || !token) { console.error("usage: fetch-fixtures <base-url> <token>"); process.exit(2); }
const headers = { Authorization: `Bearer ${token}` };
const get = async (p) => (await fetch(`${base}${p}`, { headers })).json();

const wanted = new Map();
const { collections } = await get("/v1/collections");
for (const c of collections) {
  for (let page = 1; page <= 20 && wanted.size < ENGINE_HOSTS.length; page++) {
    const { products } = await get(`/v1/collections/${c.slug}/products?page=${page}&per_page=100`);
    if (!products?.length) break;
    for (const p of products) {
      const src = p.images?.[0]?.src;
      if (!src) continue;
      const host = new URL(src).hostname;
      if (ENGINE_HOSTS.includes(host) && !wanted.has(host)) wanted.set(host, src);
    }
  }
}

const dir = path.join("tests", "fixtures", "src");
await mkdir(dir, { recursive: true });
const lines = [];
for (const [host, src] of [...wanted].sort()) {
  const res = await fetch(src, { headers: { "User-Agent": "Mozilla/5.0 (compatible; Plinth/fixtures)" } });
  if (!res.ok) { console.error(`skip ${host}: ${res.status}`); continue; }
  const type = res.headers.get("content-type") ?? "";
  const ext = type.includes("png") ? "png" : type.includes("webp") ? "webp" : "jpg";
  const buf = Buffer.from(await res.arrayBuffer());
  const name = `${host.replace(/^www\./, "").replace(/\./g, "-")}.${ext}`;
  await writeFile(path.join(dir, name), buf);
  lines.push(`${name}\t${src}`);
  console.log(`${name}\t${buf.length} bytes`);
}
await writeFile(path.join("tests", "fixtures", "sources.txt"), lines.join("\n") + "\n");
console.log(`${lines.length} fixtures; hosts without a sample: ${ENGINE_HOSTS.filter(h => !wanted.has(h)).join(", ") || "none"}`);
```

- [ ] **Step 2: Run it**

```bash
cd /Users/oliver/Projects/plinth
set -a; source /Users/oliver/Projects/Mahir/.env.local; set +a
node tools/fetch-fixtures.mjs "$FEED_BASE_URL" "$FEED_TOKEN"
ls -la tests/fixtures/src
```

Expected: at least nine files, each under 1 MB. If any file is over 1 MB, delete it and remove its line from `sources.txt`; a fixture set must stay small enough to clone happily. If fewer than nine hosts have samples today, that is fine; note the count in `tests/fixtures/README.md`.

- [ ] **Step 3: Write the fixtures README**

`tests/fixtures/README.md`:

```markdown
# Golden fixtures

One real pack shot per retailer CDN host the engine handles, pulled from the
live feed by `tools/fetch-fixtures.mjs`. `sources.txt` records the URLs.
`golden/` holds the expected result for each (written by the golden tests
with `PLINTH_UPDATE_GOLDEN=1`).

These images are retailer property used here solely as test inputs for an
image-processing tool and are not redistributed for any other purpose.
Re-fetch rather than edit; if a host changes its imagery, refresh the file
and regenerate its golden record.
```

- [ ] **Step 4: Write the failing test and the fixtures helper**

Add to `tests/Plinth.Tests/Plinth.Tests.csproj`:

```xml
  <ItemGroup>
    <None Include="fixtures/**" CopyToOutputDirectory="PreserveNewest" LinkBase="fixtures" />
  </ItemGroup>
```

`tests/Plinth.Tests/Fixtures.cs`:

```csharp
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
```

`tests/Plinth.Tests/FixturesTests.cs`:

```csharp
namespace Plinth.Tests;

public class FixturesTests
{
    [Fact]
    public void At_least_nine_engine_host_fixtures_are_present_and_small()
    {
        var all = Fixtures.All().ToList();
        Assert.True(all.Count >= 9, $"only {all.Count} fixtures");
        Assert.All(all, f => Assert.InRange(f.Bytes.Length, 1_000, 1_000_000));
    }
}
```

If Step 2 produced fewer than nine, change the `9` in the test to the count you got and say so in the commit message.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Golden fixtures: one pack shot per engine host from the live feed"
```

---

### Task 5: Source inspection and the pixel cap

**Files:**
- Create: `src/Plinth.Core/SourceInspector.cs`
- Create: `tests/Plinth.Tests/Synthetic.cs`
- Test: `tests/Plinth.Tests/SourceInspectorTests.cs`

**Interfaces:**
- Produces: `SourceInfo` (record: `string Format`, `int Width`, `int Height`, `bool HasAlpha`, `int Pages`, `int Orientation`), `SourceInspector.Inspect(byte[] bytes, int maxPixels = SourceInspector.MaxPixels)` → `SourceInfo`, throws `PlinthException` on undecodable or oversized input.
- Produces (tests): `Synthetic.PackShot(int w, int h, Rgb ground, int boxLeft, int boxTop, int boxW, int boxH, Rgb boxColour, string format = "jpeg")` → byte[], `Synthetic.TransparentPackShot(...)` → PNG bytes with alpha 0 around the box.

Note on `Format`: libvips names loaders `VipsForeignLoadJpegBuffer`, `VipsForeignLoadPngBuffer`, `VipsForeignLoadWebpBuffer`, `VipsForeignLoadGifBuffer`, `VipsForeignLoadHeifBuffer`, `VipsForeignLoadSvgBuffer`, etc. `Format` is the lowercase middle word (`jpeg`, `png`, `webp`, `gif`, `heif`).

- [ ] **Step 1: Write the synthetic helper**

`tests/Plinth.Tests/Synthetic.cs`:

```csharp
using NetVips;
using Plinth.Core;

namespace Plinth.Tests;

/// <summary>Draws deterministic test images so tests never depend on fixtures.</summary>
public static class Synthetic
{
    public static byte[] PackShot(int w, int h, Rgb ground, int boxLeft, int boxTop, int boxW, int boxH,
        Rgb boxColour, string format = "jpeg", int quality = 92)
    {
        using var bg = Image.Black(w, h, bands: 3) + ground.ToVips();
        using var box = Image.Black(boxW, boxH, bands: 3) + boxColour.ToVips();
        using var img = bg.Insert(box, boxLeft, boxTop).Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        return format switch
        {
            "jpeg" => img.JpegsaveBuffer(q: quality),
            "png" => img.PngsaveBuffer(),
            "webp" => img.WebpsaveBuffer(q: quality),
            _ => throw new ArgumentException(format),
        };
    }

    /// <summary>A box on a fully transparent ground, as PNG.</summary>
    public static byte[] TransparentPackShot(int w, int h, int boxLeft, int boxTop, int boxW, int boxH, Rgb boxColour)
    {
        using var alpha = Image.Black(w, h, bands: 1);
        using var boxAlpha = Image.Black(boxW, boxH, bands: 1) + 255.0;
        using var a = alpha.Insert(boxAlpha, boxLeft, boxTop);
        using var rgb = Image.Black(w, h, bands: 3);
        using var box = Image.Black(boxW, boxH, bands: 3) + boxColour.ToVips();
        using var colour = rgb.Insert(box, boxLeft, boxTop);
        using var img = colour.Bandjoin(a).Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        return img.PngsaveBuffer();
    }

    /// <summary>A plain image of one colour (no product).</summary>
    public static byte[] Flat(int w, int h, Rgb colour, string format = "jpeg")
    {
        using var img = (Image.Black(w, h, bands: 3) + colour.ToVips()).Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        return format == "png" ? img.PngsaveBuffer() : img.JpegsaveBuffer(q: 92);
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Plinth.Tests/SourceInspectorTests.cs`:

```csharp
using Plinth.Core;

namespace Plinth.Tests;

public class SourceInspectorTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");

    [Fact]
    public void Reports_format_size_and_alpha_for_jpeg()
    {
        var info = SourceInspector.Inspect(Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black));
        Assert.Equal("jpeg", info.Format);
        Assert.Equal(800, info.Width);
        Assert.Equal(1000, info.Height);
        Assert.False(info.HasAlpha);
        Assert.Equal(1, info.Pages);
    }

    [Fact]
    public void Reports_alpha_for_transparent_png()
    {
        var info = SourceInspector.Inspect(Synthetic.TransparentPackShot(400, 500, 100, 100, 100, 200, Black));
        Assert.Equal("png", info.Format);
        Assert.True(info.HasAlpha);
    }

    [Fact]
    public void Rejects_images_over_the_pixel_cap_before_decoding()
    {
        var bytes = Synthetic.PackShot(200, 200, White, 50, 50, 50, 50, Black);
        var ex = Assert.Throws<PlinthException>(() => SourceInspector.Inspect(bytes, maxPixels: 10_000));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void Rejects_bytes_that_are_not_an_image()
    {
        var ex = Assert.Throws<PlinthException>(() => SourceInspector.Inspect("not an image"u8.ToArray()));
        Assert.Contains("not a supported image", ex.Message);
    }

    [Fact]
    public void Real_fixtures_all_inspect()
    {
        foreach (var (name, bytes) in Fixtures.All())
        {
            var info = SourceInspector.Inspect(bytes);
            Assert.True(info.Width > 100 && info.Height > 100, name);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build error, `SourceInspector` does not exist.

- [ ] **Step 4: Write the implementation**

`src/Plinth.Core/SourceInspector.cs`:

```csharp
using NetVips;

namespace Plinth.Core;

public sealed record SourceInfo(string Format, int Width, int Height, bool HasAlpha, int Pages, int Orientation);

/// <summary>Header facts about a source, read without decoding pixels.</summary>
public static class SourceInspector
{
    /// <summary>Refuse anything larger than this before decoding (decompression bombs).</summary>
    public const int MaxPixels = 30_000_000;

    public static SourceInfo Inspect(byte[] bytes, int maxPixels = MaxPixels)
    {
        Engine.Init();
        var loader = Image.FindLoadBuffer(bytes)
            ?? throw new PlinthException("not a supported image");
        var format = LoaderToFormat(loader);

        Image img;
        try
        {
            // Loading is lazy: libvips reads the header only until pixels are asked for.
            img = Image.NewFromBuffer(bytes, access: Enums.Access.Sequential);
        }
        catch (VipsException e)
        {
            throw new PlinthException("not a supported image", e);
        }
        using (img)
        {
            var pages = img.GetTypeOf("n-pages") != IntPtr.Zero && img.Get("n-pages") is int n ? n : 1;
            var orientation = img.GetTypeOf("orientation") != IntPtr.Zero && img.Get("orientation") is int o ? o : 1;
            var width = img.Width;
            var height = pages > 1 && img.GetTypeOf("page-height") != IntPtr.Zero && img.Get("page-height") is int ph
                ? ph : img.Height;
            if ((long)width * height > maxPixels)
                throw new PlinthException($"source too large: {width}x{height} exceeds {maxPixels} pixels");
            return new SourceInfo(format, width, height, img.HasAlpha(), pages, orientation);
        }
    }

    private static string LoaderToFormat(string loader)
    {
        // "VipsForeignLoadJpegBuffer" -> "jpeg"
        const string prefix = "VipsForeignLoad";
        var s = loader.StartsWith(prefix, StringComparison.Ordinal) ? loader[prefix.Length..] : loader;
        foreach (var suffix in new[] { "Buffer", "File", "Source" })
            if (s.EndsWith(suffix, StringComparison.Ordinal)) { s = s[..^suffix.Length]; break; }
        return s.ToLowerInvariant();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass. If `Get("n-pages")` returns a type other than `int` on this build, print `img.Get("n-pages")?.GetType()` once in a scratch test to see the boxed type, adjust the pattern, and remove the scratch test.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Source inspection with the pixel cap and synthetic test images"
```

---

### Task 6: Measure the ground and find the trim box

**Files:**
- Create: `src/Plinth.Core/Measurer.cs`
- Test: `tests/Plinth.Tests/MeasurerTests.cs`

**Interfaces:**
- Consumes: `Recipe`, `Rgb`, `SourceInfo`.
- Produces: `GroundInfo(Rgb Sampled, int CornerSpread, bool CornersAgree)`, `Box(int Left, int Top, int Width, int Height)` with `Right`, `Bottom`, `TouchesEdges(int w, int h)` → count, `Measurement(GroundInfo Ground, Box Box, bool TrimIsNoop, double ContentShareBefore, int MeasureWidth, int MeasureHeight)`, `Measurer.MeasureSide = 512`, `Measurer.Measure(byte[] bytes, SourceInfo info, Recipe recipe)` → `Measurement` in **source pixel coordinates**.

Algorithm:
1. `Image.ThumbnailBuffer(bytes, MeasureSide, height: MeasureSide, size: Enums.Size.Down, outputProfile: "srgb", intent: Enums.Intent.Relative)` gives a small, orientation-applied, sRGB working copy (shrink-on-load inside the decoder for JPEG).
2. If it has alpha, `Flatten(background: recipe.Background.ToVips())`.
3. If it has fewer than 3 bands, `Colourspace(Enums.Interpretation.Srgb)`.
4. Corner patches: 8×8 at each corner (or smaller if the image is tiny). Per-band mean via `Stats()` (row `b+1`, column 4 is the mean). Ground = per-channel median of the four corners. `CornerSpread` = max Chebyshev distance from any corner mean to the ground. `CornersAgree` = spread ≤ `TrimThreshold`.
5. `FindTrim(threshold: recipe.TrimThreshold, background: ground.ToVips())` → `[left, top, width, height]` in thumbnail space. Width 0 means nothing found: treat as the full frame and `TrimIsNoop = true`. A box equal to the full frame is also a no-op.
6. Scale back to source space: `scale = thumb.Width / (double)info.Width` (use the rotated dimensions if orientation is 5..8: swap width/height). Floor left/top, ceil right/bottom, clamp to the source, so the box never cuts into content.
7. `ContentShareBefore` = max(boxW / W, boxH / H) in source space.

- [ ] **Step 1: Write the failing tests**

`tests/Plinth.Tests/MeasurerTests.cs`:

```csharp
using Plinth.Core;

namespace Plinth.Tests;

public class MeasurerTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private static readonly Rgb Grey = Rgb.Parse("#c8c8c8");

    private static Measurement Measure(byte[] bytes, Recipe? recipe = null) =>
        Measurer.Measure(bytes, SourceInspector.Inspect(bytes), recipe ?? Recipe.Default);

    [Fact]
    public void Finds_the_box_on_a_white_ground_within_a_few_source_pixels()
    {
        var m = Measure(Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black));
        Assert.InRange(m.Box.Left, 194, 202);
        Assert.InRange(m.Box.Top, 294, 302);
        Assert.InRange(m.Box.Width, 298, 312);
        Assert.InRange(m.Box.Height, 398, 412);
        Assert.False(m.TrimIsNoop);
        Assert.Equal(255, m.Ground.Sampled.R);
        Assert.True(m.Ground.CornersAgree);
        Assert.InRange(m.ContentShareBefore, 0.39, 0.42);
    }

    [Fact]
    public void Samples_a_non_white_ground_and_still_trims()
    {
        var m = Measure(Synthetic.PackShot(600, 600, Grey, 100, 100, 200, 200, Black));
        Assert.InRange(m.Ground.Sampled.R, 196, 204);
        Assert.InRange(m.Box.Width, 196, 212);
        Assert.True(m.Ground.CornersAgree);
    }

    [Fact]
    public void Flattens_transparency_onto_the_recipe_background_before_measuring()
    {
        var m = Measure(Synthetic.TransparentPackShot(400, 500, 100, 150, 100, 200, Black));
        Assert.InRange(m.Box.Left, 96, 102);
        Assert.InRange(m.Box.Top, 146, 152);
        Assert.InRange(m.Box.Height, 198, 208);
    }

    [Fact]
    public void A_flat_image_is_a_noop_trim_covering_the_frame()
    {
        var m = Measure(Synthetic.Flat(300, 400, White));
        Assert.True(m.TrimIsNoop);
        Assert.Equal(new Box(0, 0, 300, 400), m.Box);
    }

    [Fact]
    public void Box_edge_count_counts_frame_edges_touched()
    {
        Assert.Equal(0, new Box(10, 10, 50, 50).TouchesEdges(100, 100));
        Assert.Equal(2, new Box(0, 0, 50, 50).TouchesEdges(100, 100));
        Assert.Equal(4, new Box(0, 0, 100, 100).TouchesEdges(100, 100));
    }

    [Fact]
    public void Real_fixtures_measure_without_throwing_and_never_exceed_the_frame()
    {
        foreach (var (name, bytes) in Fixtures.All())
        {
            var info = SourceInspector.Inspect(bytes);
            var m = Measurer.Measure(bytes, info, Recipe.Default);
            Assert.True(m.Box.Left >= 0 && m.Box.Top >= 0, name);
            Assert.True(m.Box.Right <= info.Width && m.Box.Bottom <= info.Height, name);
            Assert.True(m.Box.Width > 0 && m.Box.Height > 0, name);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build error, `Measurer`, `Measurement`, `Box` do not exist.

- [ ] **Step 3: Write the implementation**

`src/Plinth.Core/Measurer.cs`:

```csharp
using NetVips;

namespace Plinth.Core;

public sealed record GroundInfo(Rgb Sampled, int CornerSpread, bool CornersAgree);

public readonly record struct Box(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public int TouchesEdges(int frameWidth, int frameHeight) =>
        (Left == 0 ? 1 : 0) + (Top == 0 ? 1 : 0)
        + (Right >= frameWidth ? 1 : 0) + (Bottom >= frameHeight ? 1 : 0);
}

public sealed record Measurement(
    GroundInfo Ground,
    Box Box,
    bool TrimIsNoop,
    double ContentShareBefore,
    int MeasureWidth,
    int MeasureHeight);

/// <summary>
/// Samples the ground and finds the content box on a small working copy,
/// then maps the box back to source coordinates. Measuring small is about
/// ten times cheaper than trimming at full size and gives the same box.
/// </summary>
public static class Measurer
{
    public const int MeasureSide = 512;
    private const int Patch = 8;

    public static Measurement Measure(byte[] bytes, SourceInfo info, Recipe recipe)
    {
        Engine.Init();
        using var thumb = LoadWorkingCopy(bytes, recipe, MeasureSide);

        var ground = SampleGround(thumb, recipe.TrimThreshold);

        var (fullW, fullH) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        var scale = thumb.Width / (double)fullW;

        var t = thumb.FindTrim(threshold: recipe.TrimThreshold, background: ground.Sampled.ToVips());
        var noop = t[2] == 0 || t[3] == 0 || (t[0] == 0 && t[1] == 0 && t[2] == thumb.Width && t[3] == thumb.Height);

        Box box;
        if (noop)
        {
            box = new Box(0, 0, fullW, fullH);
        }
        else
        {
            var left = Math.Clamp((int)Math.Floor(t[0] / scale), 0, fullW - 1);
            var top = Math.Clamp((int)Math.Floor(t[1] / scale), 0, fullH - 1);
            var right = Math.Clamp((int)Math.Ceiling((t[0] + t[2]) / scale), left + 1, fullW);
            var bottom = Math.Clamp((int)Math.Ceiling((t[1] + t[3]) / scale), top + 1, fullH);
            box = new Box(left, top, right - left, bottom - top);
        }

        var share = Math.Max(box.Width / (double)fullW, box.Height / (double)fullH);
        return new Measurement(ground, box, noop, share, thumb.Width, thumb.Height);
    }

    /// <summary>Orientation-applied, sRGB, opaque, 3-band working copy no larger than <paramref name="side"/>.</summary>
    internal static Image LoadWorkingCopy(byte[] bytes, Recipe recipe, int side)
    {
        Image img;
        try
        {
            img = Image.ThumbnailBuffer(bytes, side, height: side, size: Enums.Size.Down,
                outputProfile: "srgb", intent: Enums.Intent.Relative);
        }
        catch (VipsException e)
        {
            throw new PlinthException("could not decode source", e);
        }
        return MakeOpaqueSrgb(img, recipe.Background);
    }

    internal static Image MakeOpaqueSrgb(Image img, Rgb background)
    {
        if (img.HasAlpha())
        {
            var flat = img.Flatten(background: background.ToVips());
            img.Dispose();
            img = flat;
        }
        if (img.Bands < 3)
        {
            var rgb = img.Colourspace(Enums.Interpretation.Srgb);
            img.Dispose();
            img = rgb;
        }
        else if (img.Bands > 3)
        {
            var rgb = img.ExtractBand(0, n: 3);
            img.Dispose();
            img = rgb;
        }
        return img;
    }

    private static GroundInfo SampleGround(Image img, int threshold)
    {
        var p = Math.Min(Patch, Math.Min(img.Width, img.Height));
        var corners = new[]
        {
            PatchMean(img, 0, 0, p),
            PatchMean(img, img.Width - p, 0, p),
            PatchMean(img, 0, img.Height - p, p),
            PatchMean(img, img.Width - p, img.Height - p, p),
        };
        var ground = new Rgb(Median(corners.Select(c => c.R)), Median(corners.Select(c => c.G)), Median(corners.Select(c => c.B)));
        var spread = corners.Max(c => c.Distance(ground));
        return new GroundInfo(ground, spread, spread <= threshold);
    }

    private static Rgb PatchMean(Image img, int x, int y, int p)
    {
        using var patch = img.ExtractArea(x, y, p, p);
        using var stats = patch.Stats();
        // stats: one row per band after row 0 (whole image); column 4 is the mean.
        return Rgb.FromDoubles(stats.Getpoint(4, 1)[0], stats.Getpoint(4, 2)[0], stats.Getpoint(4, 3)[0]);
    }

    private static byte Median(IEnumerable<byte> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return (byte)((v[1] + v[2]) / 2);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass. If the corner means come out wrong, dump `stats.Width`, `stats.Height` and the row for band 1 in a scratch test; the mean is column index 4 in libvips' `stats` output. Fix the index if this build differs and delete the scratch test.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Measure the ground and trim box on a small working copy"
```

---

### Task 7: The pack-shot verdict

**Files:**
- Create: `src/Plinth.Core/VerdictScorer.cs`
- Test: `tests/Plinth.Tests/VerdictScorerTests.cs`

**Interfaces:**
- Consumes: `Measurement`, `SourceInfo`, `Recipe`.
- Produces: `Verdict(bool PackShot, double Confidence, IReadOnlyList<string> Reasons)`, `VerdictScorer.Score(Measurement m, SourceInfo info, Recipe recipe)` → `Verdict`. Reason strings are exactly: `corners-disagree`, `ground-not-background`, `touches-edges`, `content-tiny`, `content-fills-frame`, `thin-strip`.

Scoring: start at 1.0, subtract per failed test (corners disagree 0.4; ground more than 40 from the recipe background 0.2; box touches two or more frame edges 0.3; content share before trim under 0.05 → 0.3; over 0.98 → 0.3; box aspect over 6:1 either way 0.2), clamp to [0, 1], round to 2 decimals. `PackShot` is `Confidence >= 0.5`. Reported only in v1 (spec 5.4).

- [ ] **Step 1: Write the failing tests**

`tests/Plinth.Tests/VerdictScorerTests.cs`:

```csharp
using Plinth.Core;

namespace Plinth.Tests;

public class VerdictScorerTests
{
    private static readonly SourceInfo Info = new("jpeg", 1000, 1000, false, 1, 1);
    private static Measurement M(GroundInfo g, Box b, double share) => new(g, b, false, share, 512, 512);
    private static readonly GroundInfo Clean = new(Rgb.Parse("#ffffff"), 2, true);

    [Fact]
    public void A_clean_centred_pack_shot_scores_full_confidence()
    {
        var v = VerdictScorer.Score(M(Clean, new Box(200, 200, 600, 600), 0.6), Info, Recipe.Default);
        Assert.True(v.PackShot);
        Assert.Equal(1.0, v.Confidence);
        Assert.Empty(v.Reasons);
    }

    [Fact]
    public void Disagreeing_corners_and_edge_contact_fail_the_verdict()
    {
        var busy = new GroundInfo(Rgb.Parse("#808080"), 90, false);
        var v = VerdictScorer.Score(M(busy, new Box(0, 0, 1000, 700), 1.0), Info, Recipe.Default);
        Assert.False(v.PackShot);
        Assert.Contains("corners-disagree", v.Reasons);
        Assert.Contains("ground-not-background", v.Reasons);
        Assert.Contains("touches-edges", v.Reasons);
        Assert.Contains("content-fills-frame", v.Reasons);
        Assert.Equal(0.0, v.Confidence);
    }

    [Fact]
    public void Tiny_content_and_thin_strips_lose_points_but_may_still_pass()
    {
        var v = VerdictScorer.Score(M(Clean, new Box(480, 100, 40, 800), 0.8), Info, Recipe.Default);
        Assert.Contains("thin-strip", v.Reasons);
        Assert.Equal(0.8, v.Confidence);
        var tiny = VerdictScorer.Score(M(Clean, new Box(490, 490, 20, 20), 0.02), Info, Recipe.Default);
        Assert.Contains("content-tiny", tiny.Reasons);
        Assert.Equal(0.7, tiny.Confidence);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build error, `VerdictScorer` does not exist.

- [ ] **Step 3: Write the implementation**

`src/Plinth.Core/VerdictScorer.cs`:

```csharp
namespace Plinth.Core;

public sealed record Verdict(bool PackShot, double Confidence, IReadOnlyList<string> Reasons);

/// <summary>
/// Is this a flat-ground pack shot that is safe to trim onto a card? Reported
/// on every image so the heuristic can be tuned on real traffic; in v1 the
/// host allowlist, not this score, decides what gets trimmed.
/// </summary>
public static class VerdictScorer
{
    public static Verdict Score(Measurement m, SourceInfo info, Recipe recipe)
    {
        var (w, h) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        var reasons = new List<string>();
        var score = 1.0;

        void Fail(string reason, double penalty) { reasons.Add(reason); score -= penalty; }

        if (!m.Ground.CornersAgree) Fail("corners-disagree", 0.4);
        if (m.Ground.Sampled.Distance(recipe.Background) > 40) Fail("ground-not-background", 0.2);
        if (m.Box.TouchesEdges(w, h) >= 2) Fail("touches-edges", 0.3);
        if (m.ContentShareBefore < 0.05) Fail("content-tiny", 0.3);
        else if (m.ContentShareBefore > 0.98) Fail("content-fills-frame", 0.3);
        var aspect = m.Box.Width / (double)Math.Max(1, m.Box.Height);
        if (aspect > 6 || aspect < 1.0 / 6) Fail("thin-strip", 0.2);

        var confidence = Math.Round(Math.Clamp(score, 0, 1), 2);
        return new Verdict(confidence >= 0.5, confidence, reasons);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Pack-shot verdict, reported not enforced"
```

---

### Task 8: Render: decode at need, crop, fit, canvas, encode

**Files:**
- Create: `src/Plinth.Core/Renderer.cs`
- Test: `tests/Plinth.Tests/RendererTests.cs`

**Interfaces:**
- Consumes: `Measurement`, `SourceInfo`, `Recipe`, `Measurer.MakeOpaqueSrgb`.
- Produces: `OutputInfo(int Width, int Height, int Bytes, string Format)`, `Rendered(byte[] Bytes, OutputInfo Info, int DecodeWidth, int DecodeHeight)`, `Renderer.Render(byte[] source, SourceInfo info, Measurement m, Recipe recipe)` → `Rendered`, `Renderer.DecodeSizeFor(SourceInfo info, Measurement m, Recipe recipe)` → `(int w, int h)`.

Algorithm (spec 5.5 and 12.2):
1. Decode size: the box must come out at least the content box on both axes, so `s = max(cbW / box.Width, cbH / box.Height)`; if `!Upscale` or `s > 1`, clamp `s` to 1. Decode target = `(ceil(W*s), ceil(H*s))` where W,H are orientation-applied source dims. Pass those (+1 slack each) to `ThumbnailBuffer` with `size: Down`: the decoder shrinks on load for JPEG and never enlarges.
2. `MakeOpaqueSrgb` (flatten, 3 bands).
3. Actual scale `a = decoded.Width / W`. Crop `ExtractArea(round(box.Left*a), round(box.Top*a), round(box.Width*a), round(box.Height*a))` clamped to the decoded frame. Skip when `TrimIsNoop`.
4. Fit: `f = min(cbW / crop.Width, cbH / crop.Height)`; if `!Upscale` then `f = min(f, 1)`. `Resize(f)` unless `|f - 1| < 0.001`.
5. `Gravity(Enums.CompassDirection.Centre, canvasW, canvasH, extend: Enums.Extend.Background, background: recipe.Background.ToVips())`.
6. Encode: webp → `WebpsaveBuffer(q: Quality, effort: 4, smartSubsample: false, keep: Enums.ForeignKeep.None)`; png → `PngsaveBuffer(compression: 6, keep: Enums.ForeignKeep.None)`.

- [ ] **Step 1: Write the failing tests**

`tests/Plinth.Tests/RendererTests.cs`:

```csharp
using NetVips;
using Plinth.Core;

namespace Plinth.Tests;

public class RendererTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");

    private static Rendered RenderSynthetic(Recipe recipe, int w = 800, int h = 1000)
    {
        var bytes = Synthetic.PackShot(w, h, White, 200, 300, 300, 400, Black);
        var info = SourceInspector.Inspect(bytes);
        var m = Measurer.Measure(bytes, info, recipe);
        return Renderer.Render(bytes, info, m, recipe);
    }

    [Fact]
    public void Output_is_the_recipe_canvas_with_content_fitted_and_centred()
    {
        var r = RenderSynthetic(Recipe.Default);
        Assert.Equal(1000, r.Info.Width);
        Assert.Equal(1250, r.Info.Height);
        Assert.Equal("webp", r.Info.Format);
        using var img = Image.NewFromBuffer(r.Bytes);
        var t = img.FindTrim(threshold: 12, background: [255, 255, 255]);
        // 300x400 box scaled to fit 780x975: limited by height -> 731x975
        Assert.InRange(t[3], 968, 978);
        Assert.InRange(t[2], 724, 740);
        Assert.InRange(t[0], 128, 142);
        Assert.InRange(t[1], 132, 144);
    }

    [Fact]
    public void Png_recipe_encodes_png()
    {
        var r = RenderSynthetic(Recipe.Default with { Format = "png" });
        Assert.Equal("png", r.Info.Format);
        Assert.Equal("VipsForeignLoadPngBuffer", Image.FindLoadBuffer(r.Bytes));
    }

    [Fact]
    public void Decode_size_never_exceeds_the_source_and_covers_the_content_box()
    {
        var info = new SourceInfo("jpeg", 4000, 5000, false, 1, 1);
        var m = new Measurement(new GroundInfo(White, 0, true), new Box(1000, 1000, 2000, 2500), false, 0.5, 410, 512);
        var (w, h) = Renderer.DecodeSizeFor(info, m, Recipe.Default);
        // box must reach 780x975: scale 0.39 -> 1560x1950 (+1 slack)
        Assert.InRange(w, 1560, 1562);
        Assert.InRange(h, 1950, 1952);
        var small = new SourceInfo("jpeg", 400, 500, false, 1, 1);
        var ms = new Measurement(new GroundInfo(White, 0, true), new Box(100, 100, 200, 250), false, 0.5, 400, 500);
        Assert.Equal((401, 501), Renderer.DecodeSizeFor(small, ms, Recipe.Default));
    }

    [Fact]
    public void No_upscale_leaves_small_content_small()
    {
        var r = RenderSynthetic(Recipe.Default with { Upscale = false }, 400, 500);
        using var img = Image.NewFromBuffer(r.Bytes);
        var t = img.FindTrim(threshold: 12, background: [255, 255, 255]);
        Assert.InRange(t[2], 296, 306);
        Assert.InRange(t[3], 396, 406);
    }

    [Fact]
    public void Real_fixtures_render_to_the_canvas()
    {
        foreach (var (name, bytes) in Fixtures.All())
        {
            var info = SourceInspector.Inspect(bytes);
            var m = Measurer.Measure(bytes, info, Recipe.Default);
            var r = Renderer.Render(bytes, info, m, Recipe.Default);
            Assert.Equal((1000, 1250), (r.Info.Width, r.Info.Height));
            Assert.True(r.Bytes.Length < 200_000, $"{name} is {r.Bytes.Length} bytes");
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build error, `Renderer` does not exist.

- [ ] **Step 3: Write the implementation**

`src/Plinth.Core/Renderer.cs`:

```csharp
using NetVips;

namespace Plinth.Core;

public sealed record OutputInfo(int Width, int Height, int Bytes, string Format);

public sealed record Rendered(byte[] Bytes, OutputInfo Info, int DecodeWidth, int DecodeHeight);

/// <summary>
/// Decodes only as many pixels as the content box needs, crops to the
/// measured box, fits it into the content box and centres it on the canvas.
/// </summary>
public static class Renderer
{
    public static (int w, int h) DecodeSizeFor(SourceInfo info, Measurement m, Recipe recipe)
    {
        var (w, h) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        var s = Math.Max(recipe.ContentBoxWidth / (double)m.Box.Width, recipe.ContentBoxHeight / (double)m.Box.Height);
        if (s > 1) s = 1;
        return ((int)Math.Ceiling(w * s) + 1, (int)Math.Ceiling(h * s) + 1);
    }

    public static Rendered Render(byte[] source, SourceInfo info, Measurement m, Recipe recipe)
    {
        Engine.Init();
        var (fullW, fullH) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        var (dw, dh) = DecodeSizeFor(info, m, recipe);

        Image img;
        try
        {
            img = Image.ThumbnailBuffer(source, dw, height: dh, size: Enums.Size.Down,
                outputProfile: "srgb", intent: Enums.Intent.Relative);
        }
        catch (VipsException e)
        {
            throw new PlinthException("could not decode source", e);
        }
        img = Measurer.MakeOpaqueSrgb(img, recipe.Background);
        var decodeW = img.Width;
        var decodeH = img.Height;

        try
        {
            if (!m.TrimIsNoop)
            {
                var a = img.Width / (double)fullW;
                var left = Math.Clamp((int)Math.Round(m.Box.Left * a), 0, img.Width - 1);
                var top = Math.Clamp((int)Math.Round(m.Box.Top * a), 0, img.Height - 1);
                var width = Math.Clamp((int)Math.Round(m.Box.Width * a), 1, img.Width - left);
                var height = Math.Clamp((int)Math.Round(m.Box.Height * a), 1, img.Height - top);
                var crop = img.ExtractArea(left, top, width, height);
                img.Dispose();
                img = crop;
            }

            var f = Math.Min(recipe.ContentBoxWidth / (double)img.Width, recipe.ContentBoxHeight / (double)img.Height);
            if (!recipe.Upscale) f = Math.Min(f, 1);
            if (Math.Abs(f - 1) >= 0.001)
            {
                var resized = img.Resize(f);
                img.Dispose();
                img = resized;
            }

            var canvas = img.Gravity(Enums.CompassDirection.Centre, recipe.CanvasWidth, recipe.CanvasHeight,
                extend: Enums.Extend.Background, background: recipe.Background.ToVips());
            img.Dispose();
            img = canvas;

            var bytes = recipe.Format == "png"
                ? img.PngsaveBuffer(compression: 6, keep: Enums.ForeignKeep.None)
                : img.WebpsaveBuffer(q: recipe.Quality, effort: 4, smartSubsample: false, keep: Enums.ForeignKeep.None);

            return new Rendered(bytes, new OutputInfo(recipe.CanvasWidth, recipe.CanvasHeight, bytes.Length, recipe.Format), decodeW, decodeH);
        }
        catch (VipsException e)
        {
            throw new PlinthException("render failed", e);
        }
        finally
        {
            img.Dispose();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass. The exact trim numbers in the first test are for the synthetic box; if they miss by more than the ranges, print `t` and check the centring maths before widening a range.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Render: decode at need, crop, fit, canvas, encode"
```

---

### Task 9: The result record and the Normalizer

**Files:**
- Create: `src/Plinth.Core/ResultRecord.cs`, `src/Plinth.Core/Normalizer.cs`
- Test: `tests/Plinth.Tests/NormalizerTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `ResultRecord` and its parts (below), `ResultRecord.ToJson()` → string (camelCase, no indentation), `ResultRecord.FromJson(string)`, `NormalizeResult(string Status, byte[]? Output, ResultRecord Record)` where `Status` is `ok`, `passthrough` or `failed`, `Normalizer.Normalize(byte[] source, Recipe recipe, string? sourceId = null)` → `NormalizeResult` (never throws for bad images; throws `ArgumentNullException` only for null arguments).

Passthrough (spec 12.2): after measuring, if the source aspect is within 1% of the recipe aspect, the sampled ground is within `TrimThreshold` of the recipe background, `|ContentShareBefore - ContentShare| <= 0.03`, the source format equals the recipe format and the source width is `<= CanvasWidth`, return the source bytes untouched with `Status = "passthrough"`.

Timings use `Stopwatch` and go in the record; they never influence bytes.

- [ ] **Step 1: Write the failing tests**

`tests/Plinth.Tests/NormalizerTests.cs`:

```csharp
using System.Text.Json;
using Plinth.Core;

namespace Plinth.Tests;

public class NormalizerTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private static byte[] Shot() => Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black);

    [Fact]
    public void Ok_result_carries_bytes_and_a_complete_record()
    {
        var r = Normalizer.Normalize(Shot(), Recipe.Default, "https://img1.theiconic.com.au/x.jpg");
        Assert.Equal("ok", r.Status);
        Assert.NotNull(r.Output);
        var rec = r.Record;
        Assert.Equal("ok", rec.Status);
        Assert.Null(rec.Error);
        Assert.Equal(Engine.Version, rec.EngineVersion);
        Assert.Equal(Recipe.Default.Hash, rec.RecipeHash);
        Assert.Equal(OutputKey.Compute("https://img1.theiconic.com.au/x.jpg", Recipe.Default), rec.Key);
        Assert.Equal("jpeg", rec.Source.Format);
        Assert.Equal(800, rec.Source.Width);
        Assert.Equal(64, rec.Source.Sha256.Length);
        Assert.Equal("#ffffff", rec.Ground.Sampled);
        Assert.InRange(rec.Trim.Width, 298, 312);
        Assert.True(rec.Verdict.PackShot);
        Assert.Equal(1000, rec.Output!.Width);
        Assert.Equal(r.Output!.Length, rec.Output.Bytes);
        Assert.True(rec.TimingsMs.Encode >= 0);
    }

    [Fact]
    public void Without_a_source_id_the_key_derives_from_the_bytes()
    {
        var bytes = Shot();
        var r = Normalizer.Normalize(bytes, Recipe.Default);
        Assert.Equal(OutputKey.Compute(SourceId.FromBytes(bytes), Recipe.Default), r.Record.Key);
    }

    [Fact]
    public void Bad_bytes_fail_with_a_record_and_no_output_and_do_not_throw()
    {
        var r = Normalizer.Normalize("nope"u8.ToArray(), Recipe.Default, "https://a/b.jpg");
        Assert.Equal("failed", r.Status);
        Assert.Null(r.Output);
        Assert.Equal("failed", r.Record.Status);
        Assert.Contains("not a supported image", r.Record.Error);
        Assert.Equal(OutputKey.Compute("https://a/b.jpg", Recipe.Default), r.Record.Key);
    }

    [Fact]
    public void An_already_normalised_image_passes_through_untouched()
    {
        var first = Normalizer.Normalize(Shot(), Recipe.Default);
        var again = Normalizer.Normalize(first.Output!, Recipe.Default);
        Assert.Equal("passthrough", again.Status);
        Assert.Same(first.Output, again.Output);
        Assert.Equal("passthrough", again.Record.Status);
    }

    [Fact]
    public void Record_round_trips_through_camel_case_json()
    {
        var r = Normalizer.Normalize(Shot(), Recipe.Default, "https://a/b.jpg");
        var json = r.Record.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("verdict").GetProperty("packShot").GetBoolean());
        Assert.Equal(r.Record.Key, ResultRecord.FromJson(json).Key);
        Assert.DoesNotContain("\n", json);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build error, `Normalizer` and `ResultRecord` do not exist.

- [ ] **Step 3: Write the record types**

`src/Plinth.Core/ResultRecord.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plinth.Core;

public sealed record SourceRecord(string Sha256, int Bytes, int Width, int Height, string Format, bool HadAlpha, int OrientationApplied);
public sealed record GroundRecord(string Sampled, int CornerSpread, bool CornersAgree);
public sealed record TrimRecord(int Left, int Top, int Width, int Height, bool Noop, double ContentShareBefore);
public sealed record VerdictRecord(bool PackShot, double Confidence, IReadOnlyList<string> Reasons);
public sealed record OutputRecord(int Width, int Height, int Bytes, string Format);
public sealed record TimingsRecord(long Inspect, long Measure, long Decode, long Render, long Encode, long Total)
{
    public static TimingsRecord Zero { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>Everything Plinth learned about one image. Stored beside the output.</summary>
public sealed record ResultRecord(
    string Key,
    string SourceId,
    string EngineVersion,
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
    public static GroundRecord EmptyGround { get; } = new("#000000", 0, false);
    public static TrimRecord EmptyTrim { get; } = new(0, 0, 0, 0, true, 0);
    public static VerdictRecord EmptyVerdict { get; } = new(false, 0, []);
}
```

- [ ] **Step 4: Write the Normalizer**

`src/Plinth.Core/Normalizer.cs`:

```csharp
using System.Diagnostics;
using System.Security.Cryptography;

namespace Plinth.Core;

public sealed record NormalizeResult(string Status, byte[]? Output, ResultRecord Record);

/// <summary>
/// The one entry point. Pure and deterministic: same bytes and recipe give
/// byte-identical output and an identical record (timings aside). Never
/// throws for a bad image; the record says what went wrong.
/// </summary>
public static class Normalizer
{
    public static NormalizeResult Normalize(byte[] source, Recipe recipe, string? sourceId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recipe);
        Engine.Init();

        var id = sourceId ?? SourceId.FromBytes(source);
        var key = OutputKey.Compute(id, recipe);
        var sha = Convert.ToHexStringLower(SHA256.HashData(source));
        var total = Stopwatch.StartNew();

        SourceRecord src = ResultRecord.EmptySource with { Sha256 = sha, Bytes = source.Length };
        GroundRecord ground = ResultRecord.EmptyGround;
        TrimRecord trim = ResultRecord.EmptyTrim;
        VerdictRecord verdict = ResultRecord.EmptyVerdict;
        long tInspect = 0, tMeasure = 0, tRender = 0;

        try
        {
            var sw = Stopwatch.StartNew();
            var info = SourceInspector.Inspect(source);
            tInspect = sw.ElapsedMilliseconds;
            src = new SourceRecord(sha, source.Length, info.Width, info.Height, info.Format, info.HasAlpha, info.Orientation);

            sw.Restart();
            var m = Measurer.Measure(source, info, recipe);
            tMeasure = sw.ElapsedMilliseconds;
            ground = new GroundRecord(m.Ground.Sampled.ToHex(), m.Ground.CornerSpread, m.Ground.CornersAgree);
            trim = new TrimRecord(m.Box.Left, m.Box.Top, m.Box.Width, m.Box.Height, m.TrimIsNoop, Math.Round(m.ContentShareBefore, 4));
            var v = VerdictScorer.Score(m, info, recipe);
            verdict = new VerdictRecord(v.PackShot, v.Confidence, v.Reasons);

            if (IsPassthrough(info, m, recipe))
            {
                var passRecord = new ResultRecord(key, id, Engine.Version, recipe.Hash, "passthrough", null,
                    src, ground, trim, verdict,
                    new OutputRecord(info.Width, info.Height, source.Length, info.Format),
                    new TimingsRecord(tInspect, tMeasure, 0, 0, 0, total.ElapsedMilliseconds));
                return new NormalizeResult("passthrough", source, passRecord);
            }

            sw.Restart();
            var rendered = Renderer.Render(source, info, m, recipe);
            tRender = sw.ElapsedMilliseconds;

            var output = new OutputRecord(rendered.Info.Width, rendered.Info.Height, rendered.Info.Bytes, rendered.Info.Format);
            var record = new ResultRecord(key, id, Engine.Version, recipe.Hash, "ok", null,
                src, ground, trim, verdict, output,
                new TimingsRecord(tInspect, tMeasure, 0, tRender, 0, total.ElapsedMilliseconds));
            return new NormalizeResult("ok", rendered.Bytes, record);
        }
        catch (PlinthException e)
        {
            var record = new ResultRecord(key, id, Engine.Version, recipe.Hash, "failed", e.Message,
                src, ground, trim, verdict, null,
                new TimingsRecord(tInspect, tMeasure, 0, tRender, 0, total.ElapsedMilliseconds));
            return new NormalizeResult("failed", null, record);
        }
    }

    private static bool IsPassthrough(SourceInfo info, Measurement m, Recipe recipe)
    {
        if (info.Format != recipe.Format || info.Width > recipe.CanvasWidth || info.HasAlpha) return false;
        var srcAspect = info.Width / (double)info.Height;
        var want = recipe.CanvasWidth / (double)recipe.CanvasHeight;
        if (Math.Abs(srcAspect - want) / want > 0.01) return false;
        if (m.Ground.Sampled.Distance(recipe.Background) > recipe.TrimThreshold) return false;
        return Math.Abs(m.ContentShareBefore - recipe.ContentShare) <= 0.03;
    }
}
```

The spec's record has separate decode and encode timings; the renderer does both inside one libvips pipeline, so `Decode` and `Encode` stay 0 and `Render` carries the pipeline time. Keep the fields so the JSON shape is stable when they are split later.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Normalizer with the result record, passthrough and failure posture"
```

---

### Task 10: Perceptual hash, golden records and determinism

**Files:**
- Create: `src/Plinth.Core/PerceptualHash.cs`
- Create: `tests/Plinth.Tests/GoldenTests.cs`, `tests/fixtures/golden/*.json` (generated)

**Interfaces:**
- Produces: `PerceptualHash.DHash(byte[] imageBytes)` → `ulong` (9×8 greyscale difference hash), `PerceptualHash.Distance(ulong a, ulong b)` → int (Hamming), `PerceptualHash.ToHex(ulong)`.

Golden file shape (`tests/fixtures/golden/<fixture-name>.json`):

```json
{"trim":{"left":310,"top":120,"width":980,"height":1690},"ground":"#fefefe","packShot":true,"output":{"width":1000,"height":1250},"dhash":"3c7e7e7e3c000000"}
```

The golden test compares trim box exactly, ground within a Chebyshev distance of 2, pack-shot flag exactly, output size exactly, and dHash within a Hamming distance of 3. Set `PLINTH_UPDATE_GOLDEN=1` to (re)write the files.

- [ ] **Step 1: Write the failing tests**

`tests/Plinth.Tests/GoldenTests.cs`:

```csharp
using System.Text.Json;
using Plinth.Core;

namespace Plinth.Tests;

public class GoldenTests
{
    private sealed record Golden(TrimRecord Trim, string Ground, bool PackShot, OutputRecord Output, string Dhash);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void Dhash_is_stable_and_distance_is_hamming()
    {
        var a = PerceptualHash.DHash(Synthetic.PackShot(400, 500, Rgb.Parse("#ffffff"), 100, 100, 150, 200, Rgb.Parse("#000000")));
        var b = PerceptualHash.DHash(Synthetic.PackShot(400, 500, Rgb.Parse("#ffffff"), 100, 100, 150, 200, Rgb.Parse("#000000"), quality: 60));
        var c = PerceptualHash.DHash(Synthetic.PackShot(400, 500, Rgb.Parse("#ffffff"), 250, 300, 60, 60, Rgb.Parse("#000000")));
        Assert.Equal(0, PerceptualHash.Distance(a, a));
        Assert.True(PerceptualHash.Distance(a, b) <= 2);
        Assert.True(PerceptualHash.Distance(a, c) > 6);
        Assert.Equal(16, PerceptualHash.ToHex(a).Length);
    }

    [Fact]
    public void Same_input_twice_gives_identical_bytes_and_record()
    {
        var bytes = Fixtures.All().First().Bytes;
        var a = Normalizer.Normalize(bytes, Recipe.Default, "https://x/a.jpg");
        var b = Normalizer.Normalize(bytes, Recipe.Default, "https://x/a.jpg");
        Assert.Equal(a.Output, b.Output);
        // Records hold lists (verdict reasons), so compare their JSON rather than rely on reference equality.
        Assert.Equal((a.Record with { TimingsMs = TimingsRecord.Zero }).ToJson(),
                     (b.Record with { TimingsMs = TimingsRecord.Zero }).ToJson());
    }

    [Fact]
    public void Every_fixture_matches_its_golden_record()
    {
        Directory.CreateDirectory(Fixtures.GoldenDir);
        var update = Environment.GetEnvironmentVariable("PLINTH_UPDATE_GOLDEN") == "1";
        var failures = new List<string>();

        foreach (var (name, bytes) in Fixtures.All())
        {
            var r = Normalizer.Normalize(bytes, Recipe.Default, "https://fixture/" + name);
            Assert.Equal("ok", r.Status);
            var actual = new Golden(r.Record.Trim, r.Record.Ground.Sampled, r.Record.Verdict.PackShot,
                r.Record.Output!, PerceptualHash.ToHex(PerceptualHash.DHash(r.Output!)));
            var path = Path.Combine(Fixtures.GoldenDir, Path.ChangeExtension(name, ".json"));

            if (update || !File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(actual, Json));
                File.WriteAllText(Path.Combine(SourceGoldenDir(), Path.GetFileName(path)), JsonSerializer.Serialize(actual, Json));
                continue;
            }

            var expected = JsonSerializer.Deserialize<Golden>(File.ReadAllText(path), Json)!;
            if (expected.Trim != actual.Trim) failures.Add($"{name}: trim {actual.Trim} != {expected.Trim}");
            if (Rgb.Parse(expected.Ground).Distance(Rgb.Parse(actual.Ground)) > 2) failures.Add($"{name}: ground {actual.Ground} != {expected.Ground}");
            if (expected.PackShot != actual.PackShot) failures.Add($"{name}: packShot {actual.PackShot} != {expected.PackShot}");
            if ((expected.Output.Width, expected.Output.Height) != (actual.Output.Width, actual.Output.Height)) failures.Add($"{name}: size");
            var d = PerceptualHash.Distance(Convert.ToUInt64(expected.Dhash, 16), Convert.ToUInt64(actual.Dhash, 16));
            if (d > 3) failures.Add($"{name}: dhash distance {d}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>The checked-in golden dir (not the bin copy), so updates land in git.</summary>
    private static string SourceGoldenDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Plinth.Tests.csproj"))) dir = dir.Parent;
        var golden = Path.Combine(dir!.FullName, "fixtures", "golden");
        Directory.CreateDirectory(golden);
        return golden;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build error, `PerceptualHash` does not exist.

- [ ] **Step 3: Write the implementation**

`src/Plinth.Core/PerceptualHash.cs`:

```csharp
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
```

- [ ] **Step 4: Generate the golden files, then run the tests to verify they pass**

```bash
PLINTH_UPDATE_GOLDEN=1 dotnet test
ls tests/fixtures/golden
dotnet test
```

Expected: the first run writes one JSON per fixture into `tests/fixtures/golden` (the source tree, via `SourceGoldenDir`), the second run passes with no updates. Open two or three golden files and sanity-check them by eye against the source image: the trim box should sit around the product, `packShot` should be true for every retailer pack shot. If a fixture comes out `packShot: false`, look at its `reasons` in a scratch print; a real pack shot scoring under 0.5 is a finding to note in the commit message, not something to fix by loosening the scorer.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Golden records for every fixture and a determinism test"
```

---

### Task 11: Benchmark harness and baseline

**Files:**
- Create: `src/Plinth.Bench/Plinth.Bench.csproj`, `src/Plinth.Bench/Program.cs`
- Create: `docs/bench/baseline.json` (generated)
- Modify: `Plinth.sln`, `README.md`

**Interfaces:**
- Consumes: `Normalizer.Normalize`, the fixtures directory.
- Produces: `dotnet run --project src/Plinth.Bench -- [--fixtures <dir>] [--iterations 20] [--write-baseline] [--baseline docs/bench/baseline.json]`. Exit code 1 when any measure regresses more than 20% against the baseline.

Measures per fixture over N iterations after one warm-up: wall p50/p95 (ms), CPU per image (ms, from `Process.TotalProcessorTime` delta ÷ iterations), output bytes; plus peak working set (MB) for the whole run. Writes `docs/bench/latest.json` (git-ignored) and compares against `docs/bench/baseline.json` when present. Budgets from spec 12.1 are printed beside the numbers; they are informational until the baseline exists.

- [ ] **Step 1: Create the project**

```bash
dotnet new console -n Plinth.Bench -o src/Plinth.Bench -f net10.0
dotnet sln add src/Plinth.Bench/Plinth.Bench.csproj
dotnet add src/Plinth.Bench/Plinth.Bench.csproj reference src/Plinth.Core/Plinth.Core.csproj
dotnet add src/Plinth.Bench/Plinth.Bench.csproj package NetVips.Native.osx-arm64
dotnet add src/Plinth.Bench/Plinth.Bench.csproj package NetVips.Native.linux-x64
```

Remove any `Version=` attributes `dotnet add package` wrote into the csproj (central versions).

- [ ] **Step 2: Write the harness**

`src/Plinth.Bench/Program.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using Plinth.Core;

var args_ = args.ToList();
string Opt(string name, string def) { var i = args_.IndexOf(name); return i >= 0 && i + 1 < args_.Count ? args_[i + 1] : def; }
var fixtures = Opt("--fixtures", Path.Combine("tests", "fixtures", "src"));
var iterations = int.Parse(Opt("--iterations", "20"));
var baselinePath = Opt("--baseline", Path.Combine("docs", "bench", "baseline.json"));
var writeBaseline = args_.Contains("--write-baseline");

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
var files = Directory.EnumerateFiles(fixtures).OrderBy(f => f, StringComparer.Ordinal).ToList();
if (files.Count == 0) { Console.Error.WriteLine($"no fixtures in {fixtures}"); return 2; }

Engine.Init();
var proc = Process.GetCurrentProcess();
var results = new List<FixtureResult>();

foreach (var file in files)
{
    var bytes = File.ReadAllBytes(file);
    var name = Path.GetFileName(file);
    var warm = Normalizer.Normalize(bytes, Recipe.Default, "https://bench/" + name);
    if (warm.Status == "failed") { Console.Error.WriteLine($"{name}: {warm.Record.Error}"); continue; }

    var walls = new List<double>(iterations);
    proc.Refresh();
    var cpu0 = proc.TotalProcessorTime;
    var outBytes = 0;
    for (var i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        var r = Normalizer.Normalize(bytes, Recipe.Default, "https://bench/" + name);
        walls.Add(sw.Elapsed.TotalMilliseconds);
        outBytes = r.Output!.Length;
    }
    proc.Refresh();
    var cpuPerImage = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / iterations;
    walls.Sort();
    results.Add(new FixtureResult(name, bytes.Length, outBytes,
        Math.Round(walls[walls.Count / 2], 1), Math.Round(walls[(int)(walls.Count * 0.95) - 1], 1), Math.Round(cpuPerImage, 1)));
}

proc.Refresh();
var summary = new BenchSummary(
    DateTime.UtcNow.ToString("O"), Engine.Version, Engine.LibvipsVersion, Environment.ProcessorCount, iterations,
    Math.Round(results.Average(r => r.CpuMs), 1),
    Math.Round(results.Max(r => r.WallP95Ms), 1),
    Math.Round(proc.PeakWorkingSet64 / 1048576.0, 1),
    results.Max(r => r.OutputBytes),
    results);

Console.WriteLine($"{"fixture",-40} {"in KB",7} {"out KB",7} {"p50 ms",7} {"p95 ms",7} {"cpu ms",7}");
foreach (var r in results)
    Console.WriteLine($"{r.Name,-40} {r.InputBytes / 1024,7} {r.OutputBytes / 1024,7} {r.WallP50Ms,7} {r.WallP95Ms,7} {r.CpuMs,7}");
Console.WriteLine();
Console.WriteLine($"mean cpu/image {summary.MeanCpuMs} ms (budget 40)   max p95 {summary.MaxWallP95Ms} ms (budget 120)   peak RSS {summary.PeakWorkingSetMb} MB   max output {summary.MaxOutputBytes / 1024} KB (budget 80)");

Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
File.WriteAllText(Path.Combine(Path.GetDirectoryName(baselinePath)!, "latest.json"), JsonSerializer.Serialize(summary, json));

if (writeBaseline || !File.Exists(baselinePath))
{
    File.WriteAllText(baselinePath, JsonSerializer.Serialize(summary, json));
    Console.WriteLine($"baseline written to {baselinePath}");
    return 0;
}

var baseline = JsonSerializer.Deserialize<BenchSummary>(File.ReadAllText(baselinePath), json)!;
var regressions = new List<string>();
void Check(string what, double now, double before) { if (before > 0 && now > before * 1.2) regressions.Add($"{what}: {now} vs baseline {before}"); }
Check("mean cpu/image", summary.MeanCpuMs, baseline.MeanCpuMs);
Check("max p95", summary.MaxWallP95Ms, baseline.MaxWallP95Ms);
Check("max output bytes", summary.MaxOutputBytes, baseline.MaxOutputBytes);
if (regressions.Count > 0)
{
    Console.Error.WriteLine("REGRESSION\n" + string.Join("\n", regressions));
    return 1;
}
Console.WriteLine("within 20% of baseline");
return 0;

record FixtureResult(string Name, int InputBytes, int OutputBytes, double WallP50Ms, double WallP95Ms, double CpuMs);
record BenchSummary(string RunAt, string EngineVersion, string Libvips, int Cores, int Iterations,
    double MeanCpuMs, double MaxWallP95Ms, double PeakWorkingSetMb, int MaxOutputBytes, List<FixtureResult> Fixtures);
```

- [ ] **Step 3: Run it and write the baseline**

```bash
dotnet run -c Release --project src/Plinth.Bench -- --write-baseline
dotnet run -c Release --project src/Plinth.Bench
```

Expected: a table, the summary line, `baseline written`, then on the second run `within 20% of baseline`. Record the summary numbers; they replace the spec's provisional budgets.

- [ ] **Step 4: Note the measured numbers in the spec's open items and the README**

Append to the last bullet of `docs/2026-09-02-plinth-design.md` section 15 the measured mean CPU per image, max p95 and max output size from Step 3, with the date and machine (`Apple Silicon, <cores> cores`).

Add to `README.md`:

```markdown
## Benchmark

`dotnet run -c Release --project src/Plinth.Bench` normalises every fixture
twenty times and compares against `docs/bench/baseline.json`; it exits
non-zero on a regression over 20%. `--write-baseline` resets the baseline
after an intentional change.
```

- [ ] **Step 5: Run the full test suite one last time**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Benchmark harness with a committed baseline"
```

---

## Self-review against the spec

- **4.1 Recipe**: Task 2, including the `upscale` field and validation.
- **4.2 Key**: Task 3; URL-addressable, content hash in the record (Task 9).
- **4.3 Normalize**: Task 9; purity checked by the determinism test in Task 10.
- **4.4 Record**: Task 9. `decode`/`encode` timings stay 0 for now because one libvips pipeline does both; the fields exist so the shape is stable.
- **5.2 Normalise**: orientation and sRGB via `ThumbnailBuffer` (Tasks 6, 8), pixel cap and alpha detection (Task 5), flatten (Task 6), first frame only (thumbnail loads page 0).
- **5.3 Measure and trim**: Task 6, including measure-small and outward rounding.
- **5.4 Verdict**: Task 7, reported only.
- **5.5 Canvas and encode**: Task 8.
- **5.6 Failure posture**: Task 9 returns a failed record; the redirect belongs to the API in the next plan.
- **11 Testing**: golden fixtures (Tasks 4, 10), determinism (10), policy tests that belong to Core (5, 6), verdict (7). Fetch-policy, CLI and API tests are the next plan's.
- **12 Cost**: shrink-on-load and crop-before-scale (8), passthrough and no-op exit (9), cache off and concurrency pinned (1), encoder settings (8), bench with a 20% gate (11).
- **Not in this plan** (next plan): 5.1 fetch policy, 6 front doors, 7 stores, 8 deployment, 9 Mahir integration, 10 security beyond caps.
