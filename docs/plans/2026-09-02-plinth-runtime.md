# Plinth Runtime Implementation Plan (API, CLI, stores, Docker, CI)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the two front doors on `Plinth.Core` — an HTTP API for on-demand use and a CLI for batch runs — with output stores (filesystem, Azure Blob), the fetch policy, request signing, one Docker image that runs both, and CI that tests and publishes it.

**Architecture:** `Plinth.Core` stays pure (bytes in, bytes out) and only gains a `CancellationToken` and a failed-record factory. A new `Plinth.Pipeline` class library owns every side effect behind two interfaces: `ISourceFetcher` (HTTP with the allowlist, redirect re-check, private-address block and byte cap) and `IOutputStore` (filesystem, Azure Blob, memory, null). Its `PlinthPipeline` service does "check store by key → fetch → normalise → store" once, and both `Plinth.Api` (ASP.NET minimal API) and `Plinth.Cli` (System.CommandLine, two-stage fetch/process pipeline) are thin wrappers over it. The CLI project references the API project so `plinth api` starts the server in-process, giving one container image with two modes.

**Tech Stack:** .NET 10 SDK 10.0.100, ASP.NET Core 10 (framework reference), System.CommandLine 2.0.11, Azure.Storage.Blobs 12.29.2, Azure.Identity 1.21.0, Microsoft.AspNetCore.Mvc.Testing 10.0.11 (brings TestHost), NetVips 3.2.0 with natives 8.18.6 (osx-arm64, linux-x64, linux-arm64), Docker 29, GitHub Actions, GHCR.

**Spec:** `docs/2026-09-02-plinth-design.md` sections 3, 5.1, 5.6, 6, 7, 8, 10, 12.3, 12.5. Section 9 (the Mahir swap) is a separate plan in the Mahir repo after this ships.

## Global Constraints

- Repository root `/Users/oliver/Projects/plinth`, branch off `main` (PR #1 merged first, or branch from `core` and rebase — the executor checks `git log --oneline -1 main` and reports which). `dotnet` is at `~/.local/bin/dotnet`: `export PATH="$HOME/.local/bin:$PATH"`.
- `net10.0`, Nullable on, ImplicitUsings on, TreatWarningsAsErrors on, central package management. New package versions go in `Directory.Packages.props` exactly: `System.CommandLine` 2.0.11, `Azure.Storage.Blobs` 12.29.2, `Azure.Identity` 1.21.0, `Microsoft.AspNetCore.Mvc.Testing` 10.0.11, `NetVips.Native.linux-arm64` 8.18.6 (verify it exists on NuGet in Task 8 before adding; if not, the image is linux/amd64 only).
- `Plinth.Core` must keep zero I/O and zero network. Everything with a side effect lives in `Plinth.Pipeline` or above.
- Fetch policy exactly (spec 5.1): https only; hostname must be on the allowlist by exact match; an empty allowlist refuses everything; at most 3 redirects, every hop re-checked against the allowlist and the private-address block; 12 MB body cap checked on `Content-Length` and again while reading; 12 s timeout; `User-Agent: Mozilla/5.0 (compatible; Plinth/1.0)`.
- Private addresses blocked (spec 5.1): IPv4 loopback 127/8, 10/8, 172.16/12, 192.168/16, link-local 169.254/16 (includes cloud metadata 169.254.169.254), CGNAT 100.64/10, 0.0.0.0/8, multicast/broadcast; IPv6 ::1, ::, fc00::/7, fe80::/10, multicast ff00::/8; IPv4-mapped IPv6 judged as their IPv4.
- Store layout (spec 7): `<key[0..2]>/<key>.<ext>` beside `<key[0..2]>/<key>.json`; ext from `ImageFormats.ExtensionFor`; Azure uploads carry `Content-Type` and `Cache-Control: public, max-age=31536000, immutable`.
- Environment (spec 6.1): `PLINTH_ALLOWED_HOSTS` (comma list), `PLINTH_SIGNING_KEY` (optional), `PLINTH_STORE` (`none` | `fs://<path>` | `azblob://<container>`), `PLINTH_ON_FAILURE` (`redirect` | `error`, default `redirect`), `PLINTH_RECIPES` (path to a JSON object of name → recipe; `default` always present), `PLINTH_CONCURRENCY` (already read by `Engine.Init`), plus `PLINTH_AZURE_STORAGE_CONNECTION` (connection string) or `PLINTH_AZURE_STORAGE_ACCOUNT` (account URL, uses `DefaultAzureCredential`).
- API routes and headers exactly as spec 6.1: `GET /v1/image`, `GET /v1/inspect`, `POST /v1/normalize`, `GET /healthz`, `GET /version`; headers `X-Plinth-Key`, `X-Plinth-Status`, `X-Plinth-Verdict`, `X-Plinth-Confidence`, plus `X-Plinth-Cache: hit|miss`; `Cache-Control: public, max-age=31536000, immutable` on image responses; failure → `302` to the source when `PLINTH_ON_FAILURE=redirect`, else `502` with the record as JSON.
- CLI surface exactly as spec 6.2: `plinth run --input <file|dir|-> --output <dir|azblob://container> [--recipe <name|file>] [--concurrency N] [--manifest out.jsonl] [--refresh]`, `plinth inspect <file|url>`, `plinth key <file|url> [--recipe …]`, `plinth version`, `plinth api`. Exit code non-zero only when the run itself broke or usage is wrong (2); per-image failures go to the manifest.
- Commits: one line, present tense, identity `Oliver Bingemann <ollie.bingemann@gmail.com>`, **no trailer of any kind** (owner ruling 2 Sep 2026: no tool attribution anywhere — commits, PR bodies, docs).
- Every task ends with `dotnet test` green and a commit.

---

## File structure

```
Directory.Packages.props                    + the five packages above
Plinth.sln                                  + Pipeline, Api, Cli projects
src/Plinth.Core/Normalizer.cs               CancellationToken between stages
src/Plinth.Core/ResultRecord.cs             ResultRecord.Failed(...) factory
src/Plinth.Pipeline/
  Plinth.Pipeline.csproj
  Stores/IOutputStore.cs                    interface + StoredOutput + StoreLayout
  Stores/MemoryStore.cs
  Stores/NullStore.cs
  Stores/FileSystemStore.cs
  Stores/AzureBlobStore.cs
  Stores/StoreUri.cs                        "none" | fs:// | azblob:// → IOutputStore
  Fetch/FetchPolicy.cs                      allowlist, caps, redirect limit
  Fetch/PrivateAddress.cs                   the block list
  Fetch/ISourceFetcher.cs                   + FetchResult
  Fetch/HttpSourceFetcher.cs                SocketsHttpHandler + ConnectCallback + manual redirects
  RequestSigning.cs                         HMAC-SHA256 over src
  RecipeCatalog.cs                          name → Recipe, "default" always present
  PipelineOptions.cs                        the env-driven config + loader
  PlinthPipeline.cs                         check store → fetch → normalise → store
src/Plinth.Api/
  Plinth.Api.csproj                         Sdk="Microsoft.NET.Sdk.Web"
  ApiHost.cs                                Build(args, options?, configureBuilder?)
  Program.cs                                ApiHost.Build(args).Run()
src/Plinth.Cli/
  Plinth.Cli.csproj                         references Pipeline + Api
  CliApp.cs                                 RootCommand factory (testable)
  RunCommand.cs                             two-stage batch pipeline + manifest
  Program.cs
tests/Plinth.Tests/
  Fakes/FakeFetcher.cs                      scripted ISourceFetcher
  Fakes/ScriptedHandler.cs                  scripted HttpMessageHandler for HttpSourceFetcher tests
  Stores/StoreLayoutTests.cs, FileSystemStoreTests.cs, MemoryStoreTests.cs, StoreUriTests.cs, AzureBlobStoreTests.cs
  Fetch/PrivateAddressTests.cs, HttpSourceFetcherTests.cs
  RequestSigningTests.cs, RecipeCatalogTests.cs, PipelineOptionsTests.cs, PlinthPipelineTests.cs
  Api/ApiTests.cs
  Cli/CliTests.cs
  Core: NormalizerTests.cs (+ cancellation), ResultRecordTests.cs (+ Failed)
Dockerfile, .dockerignore
.github/workflows/ci.yml, .github/workflows/release.yml
README.md, docs/2026-09-02-plinth-design.md (6.2 --refresh wording, 7 store URIs)
```

---

### Task 1: Core: cancellation and a failed-record factory

**Files:**
- Modify: `src/Plinth.Core/Normalizer.cs`, `src/Plinth.Core/ResultRecord.cs`
- Test: `tests/Plinth.Tests/NormalizerTests.cs`, `tests/Plinth.Tests/ResultRecordTests.cs` (create)

**Interfaces:**
- Produces: `Normalizer.Normalize(byte[] source, Recipe recipe, string? sourceId = null, CancellationToken ct = default)` — checks `ct` after inspect and after measure; a cancelled token surfaces as `OperationCanceledException` (cancellation is not a bad image, so it is NOT converted to a failed record). `ResultRecord.Failed(string key, string sourceId, Recipe recipe, string error)` → a record with `Status = "failed"`, empty parts, `Output = null`, zero timings.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Plinth.Tests/NormalizerTests.cs`:

```csharp
    [Fact]
    public void A_cancelled_token_surfaces_as_cancellation_not_a_failed_record()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Normalizer.Normalize(Shot(), Recipe.Default, "https://a/b.jpg", cts.Token));
    }
```

`tests/Plinth.Tests/ResultRecordTests.cs`:

```csharp
using Plinth.Core;

namespace Plinth.Tests;

public class ResultRecordTests
{
    [Fact]
    public void Failed_factory_builds_a_complete_failed_record()
    {
        var r = ResultRecord.Failed("k", "https://a/b.jpg", Recipe.Default, "source 404");
        Assert.Equal("failed", r.Status);
        Assert.Equal("source 404", r.Error);
        Assert.Equal("k", r.Key);
        Assert.Equal("https://a/b.jpg", r.SourceId);
        Assert.Equal(Engine.Version, r.EngineVersion);
        Assert.Equal(Recipe.Default.Hash, r.RecipeHash);
        Assert.Null(r.Output);
        var back = ResultRecord.FromJson(r.ToJson());
        Assert.Equal("failed", back.Status);
        Assert.Null(back.Output);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build errors — no 4-argument `Normalize`, no `ResultRecord.Failed`.

- [ ] **Step 3: Implement**

In `Normalizer.Normalize`, change the signature to `(byte[] source, Recipe recipe, string? sourceId = null, CancellationToken ct = default)` and add `ct.ThrowIfCancellationRequested();` as the first statement inside the `try`, again immediately after `SourceInspector.Inspect(...)`, and again immediately after `Measurer.Measure(...)`. Do not catch `OperationCanceledException` anywhere; the existing `catch (Exception e) when (e is PlinthException or VipsException)` filter already lets it through.

In `ResultRecord`, add:

```csharp
    /// <summary>A record for work that never reached the pixels (fetch failed, bad URL).</summary>
    public static ResultRecord Failed(string key, string sourceId, Recipe recipe, string error) =>
        new(key, sourceId, Engine.Version, Engine.LibvipsVersion, recipe.Hash, "failed", error,
            EmptySource, EmptyGround, EmptyTrim, EmptyVerdict, null, TimingsRecord.Zero);
```

(Match the constructor's actual parameter order in the file; `LibvipsVersion` sits after `EngineVersion`.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass (73 tests).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Core: cancellation between stages and a failed-record factory"
```

---

### Task 2: Pipeline project, store interface, memory, null and filesystem stores

**Files:**
- Create: `src/Plinth.Pipeline/Plinth.Pipeline.csproj`, `src/Plinth.Pipeline/Stores/IOutputStore.cs`, `Stores/MemoryStore.cs`, `Stores/NullStore.cs`, `Stores/FileSystemStore.cs`
- Modify: `Plinth.sln`, `tests/Plinth.Tests/Plinth.Tests.csproj`
- Test: `tests/Plinth.Tests/Stores/StoreLayoutTests.cs`, `MemoryStoreTests.cs`, `FileSystemStoreTests.cs`

**Interfaces:**
- Produces (namespace `Plinth.Pipeline.Stores`):

```csharp
public sealed record StoredOutput(byte[] Bytes, ResultRecord Record);

public interface IOutputStore
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default);
    Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default);
}

public static class StoreLayout
{
    public static string RecordPath(string key) => $"{key[..2]}/{key}.json";
    public static string ImagePath(string key, string format) => $"{key[..2]}/{key}.{ImageFormats.ExtensionFor(format)}";
}
```

`ExistsAsync` is true when the record exists. `PutAsync` with a record whose `Output` is null (failed) throws `ArgumentException` — failures are never stored. `TryGetAsync` reads the record first, derives the image path from `record.Output.Format`, and returns null if either file is missing.

- [ ] **Step 1: Create the project and wire it in**

```bash
dotnet new classlib -n Plinth.Pipeline -o src/Plinth.Pipeline -f net10.0 && rm src/Plinth.Pipeline/Class1.cs
dotnet sln add src/Plinth.Pipeline/Plinth.Pipeline.csproj
dotnet add src/Plinth.Pipeline/Plinth.Pipeline.csproj reference src/Plinth.Core/Plinth.Core.csproj
dotnet add tests/Plinth.Tests/Plinth.Tests.csproj reference src/Plinth.Pipeline/Plinth.Pipeline.csproj
```

`src/Plinth.Pipeline/Plinth.Pipeline.csproj` should end up as:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Plinth.Pipeline</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Plinth.Core/Plinth.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing tests**

`tests/Plinth.Tests/Stores/StoreLayoutTests.cs`:

```csharp
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

public class StoreLayoutTests
{
    [Fact]
    public void Paths_shard_by_the_first_two_key_characters()
    {
        var key = new string('a', 64);
        Assert.Equal($"aa/{key}.json", StoreLayout.RecordPath(key));
        Assert.Equal($"aa/{key}.webp", StoreLayout.ImagePath(key, "webp"));
        Assert.Equal($"aa/{key}.jpg", StoreLayout.ImagePath(key, "jpeg"));
    }
}
```

`tests/Plinth.Tests/Stores/MemoryStoreTests.cs` — a shared contract test both stores run:

```csharp
using Plinth.Core;
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

/// <summary>Behaviour every IOutputStore must have. Subclass per implementation.</summary>
public abstract class OutputStoreContract
{
    protected abstract IOutputStore Create();

    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");

    protected static NormalizeResult Sample() =>
        Normalizer.Normalize(Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black), Recipe.Default, "https://x/a.jpg");

    [Fact]
    public async Task Missing_key_is_absent()
    {
        var store = Create();
        Assert.False(await store.ExistsAsync(new string('b', 64)));
        Assert.Null(await store.TryGetAsync(new string('b', 64)));
    }

    [Fact]
    public async Task Put_then_get_round_trips_bytes_and_record()
    {
        var store = Create();
        var r = Sample();
        await store.PutAsync(r.Record.Key, r.Output!, r.Record);
        Assert.True(await store.ExistsAsync(r.Record.Key));
        var got = await store.TryGetAsync(r.Record.Key);
        Assert.NotNull(got);
        Assert.Equal(r.Output, got!.Bytes);
        Assert.Equal(r.Record.ToJson(), got.Record.ToJson());
    }

    [Fact]
    public async Task Failed_records_are_never_stored()
    {
        var store = Create();
        var failed = ResultRecord.Failed(new string('c', 64), "https://x/c.jpg", Recipe.Default, "nope");
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(failed.Key, [], failed));
    }
}

public class MemoryStoreTests : OutputStoreContract
{
    protected override IOutputStore Create() => new MemoryStore();
}
```

`tests/Plinth.Tests/Stores/FileSystemStoreTests.cs`:

```csharp
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

public class FileSystemStoreTests : OutputStoreContract, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plinth-fs-" + Guid.NewGuid().ToString("N"));

    protected override IOutputStore Create() => new FileSystemStore(_root);

    [Fact]
    public async Task Files_land_in_the_sharded_layout()
    {
        var store = Create();
        var r = Sample();
        await store.PutAsync(r.Record.Key, r.Output!, r.Record);
        Assert.True(File.Exists(Path.Combine(_root, StoreLayout.ImagePath(r.Record.Key, "webp"))));
        Assert.True(File.Exists(Path.Combine(_root, StoreLayout.RecordPath(r.Record.Key))));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build errors — the `Plinth.Pipeline.Stores` types do not exist.

- [ ] **Step 4: Implement**

`src/Plinth.Pipeline/Stores/IOutputStore.cs` — the interface, `StoredOutput` and `StoreLayout` exactly as in Interfaces above, with `using Plinth.Core;`.

`src/Plinth.Pipeline/Stores/MemoryStore.cs`:

```csharp
using System.Collections.Concurrent;
using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>In-process store for tests and single-run tooling.</summary>
public sealed class MemoryStore : IOutputStore
{
    private readonly ConcurrentDictionary<string, StoredOutput> _items = new();

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(_items.ContainsKey(key));

    public Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(key, out var v) ? v : null);

    public Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default)
    {
        StoreGuard.RequireStorable(record);
        _items[key] = new StoredOutput(bytes, record);
        return Task.CompletedTask;
    }
}

internal static class StoreGuard
{
    public static void RequireStorable(ResultRecord record)
    {
        if (record.Output is null || record.Status == "failed")
            throw new ArgumentException("failed records are never stored", nameof(record));
    }
}
```

`src/Plinth.Pipeline/Stores/NullStore.cs`:

```csharp
using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>Pass-through: nothing is remembered. The API's default when no store is configured.</summary>
public sealed class NullStore : IOutputStore
{
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(false);
    public Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default) => Task.FromResult<StoredOutput?>(null);
    public Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default) => Task.CompletedTask;
}
```

`src/Plinth.Pipeline/Stores/FileSystemStore.cs`:

```csharp
using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>Sharded files under one root: &lt;root&gt;/&lt;key[0..2]&gt;/&lt;key&gt;.&lt;ext&gt; and .json.</summary>
public sealed class FileSystemStore(string root) : IOutputStore
{
    public string Root { get; } = Path.GetFullPath(root);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(Path.Combine(Root, StoreLayout.RecordPath(key))));

    public async Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default)
    {
        var recordPath = Path.Combine(Root, StoreLayout.RecordPath(key));
        if (!File.Exists(recordPath)) return null;
        var record = ResultRecord.FromJson(await File.ReadAllTextAsync(recordPath, ct));
        if (record.Output is null) return null;
        var imagePath = Path.Combine(Root, StoreLayout.ImagePath(key, record.Output.Format));
        if (!File.Exists(imagePath)) return null;
        return new StoredOutput(await File.ReadAllBytesAsync(imagePath, ct), record);
    }

    public async Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default)
    {
        StoreGuard.RequireStorable(record);
        var imagePath = Path.Combine(Root, StoreLayout.ImagePath(key, record.Output!.Format));
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        // Write to a temp name and rename so a reader never sees a half-written file.
        var tmp = imagePath + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, imagePath, overwrite: true);
        var recordPath = Path.Combine(Root, StoreLayout.RecordPath(key));
        var tmpRecord = recordPath + ".tmp";
        await File.WriteAllTextAsync(tmpRecord, record.ToJson(), ct);
        File.Move(tmpRecord, recordPath, overwrite: true);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Pipeline project with the output store contract, memory, null and filesystem stores"
```

---

### Task 3: Azure Blob store and store URIs

**Files:**
- Create: `src/Plinth.Pipeline/Stores/AzureBlobStore.cs`, `src/Plinth.Pipeline/Stores/StoreUri.cs`
- Modify: `Directory.Packages.props`, `src/Plinth.Pipeline/Plinth.Pipeline.csproj`
- Test: `tests/Plinth.Tests/Stores/AzureBlobStoreTests.cs`, `tests/Plinth.Tests/Stores/StoreUriTests.cs`, `tests/Plinth.Tests/AzureFactAttribute.cs`

**Interfaces:**
- Produces: `AzureBlobStore(BlobContainerClient container)`; `AzureBlobStore.FromEnvironment(string container, Func<string, string?> env)` using `PLINTH_AZURE_STORAGE_CONNECTION` or `PLINTH_AZURE_STORAGE_ACCOUNT` + `DefaultAzureCredential`; `AzureBlobStore.CacheControl = "public, max-age=31536000, immutable"`; `StoreUri.Open(string uri, Func<string, string?> env)` → `IOutputStore` for `none`, `fs://<path>`, `azblob://<container>`; anything else throws `PlinthException`.

- [ ] **Step 1: Add packages**

Add to `Directory.Packages.props`:

```xml
    <PackageVersion Include="Azure.Storage.Blobs" Version="12.29.2" />
    <PackageVersion Include="Azure.Identity" Version="1.21.0" />
```

and to `Plinth.Pipeline.csproj`'s ItemGroup:

```xml
    <PackageReference Include="Azure.Storage.Blobs" />
    <PackageReference Include="Azure.Identity" />
```

- [ ] **Step 2: Write the failing tests**

`tests/Plinth.Tests/AzureFactAttribute.cs`:

```csharp
namespace Plinth.Tests;

/// <summary>Runs only when PLINTH_TEST_AZURE_CONNECTION names a real storage account (Azurite works too).</summary>
public sealed class AzureFactAttribute : FactAttribute
{
    public AzureFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLINTH_TEST_AZURE_CONNECTION")))
            Skip = "PLINTH_TEST_AZURE_CONNECTION not set";
    }
}
```

`tests/Plinth.Tests/Stores/AzureBlobStoreTests.cs`:

```csharp
using Azure.Storage.Blobs;
using Plinth.Core;
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

public class AzureBlobStoreTests
{
    [Fact]
    public void Blob_names_follow_the_store_layout_and_headers_are_immutable()
    {
        Assert.Equal("public, max-age=31536000, immutable", AzureBlobStore.CacheControl);
        var key = new string('d', 64);
        Assert.Equal($"dd/{key}.webp", StoreLayout.ImagePath(key, "webp"));
    }

    [AzureFact]
    public async Task Round_trips_against_a_real_container()
    {
        var conn = Environment.GetEnvironmentVariable("PLINTH_TEST_AZURE_CONNECTION")!;
        var container = new BlobContainerClient(conn, "plinth-test-" + Guid.NewGuid().ToString("N")[..8]);
        await container.CreateIfNotExistsAsync();
        try
        {
            var store = new AzureBlobStore(container);
            var r = Normalizer.Normalize(Synthetic.PackShot(800, 1000, Rgb.Parse("#ffffff"), 200, 300, 300, 400, Rgb.Parse("#000000")), Recipe.Default, "https://x/a.jpg");
            Assert.False(await store.ExistsAsync(r.Record.Key));
            await store.PutAsync(r.Record.Key, r.Output!, r.Record);
            Assert.True(await store.ExistsAsync(r.Record.Key));
            var got = await store.TryGetAsync(r.Record.Key);
            Assert.Equal(r.Output, got!.Bytes);
            var props = await container.GetBlobClient(StoreLayout.ImagePath(r.Record.Key, "webp")).GetPropertiesAsync();
            Assert.Equal("image/webp", props.Value.ContentType);
            Assert.Equal(AzureBlobStore.CacheControl, props.Value.CacheControl);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
```

`tests/Plinth.Tests/Stores/StoreUriTests.cs`:

```csharp
using Plinth.Core;
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

public class StoreUriTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void None_and_fs_open_the_right_stores()
    {
        Assert.IsType<NullStore>(StoreUri.Open("none", NoEnv));
        var fs = Assert.IsType<FileSystemStore>(StoreUri.Open("fs:///tmp/plinth-x", NoEnv));
        Assert.Equal(Path.GetFullPath("/tmp/plinth-x"), fs.Root);
        Assert.IsType<FileSystemStore>(StoreUri.Open("fs://relative/dir", NoEnv));
    }

    [Fact]
    public void Azblob_needs_credentials_and_unknown_schemes_are_refused()
    {
        Assert.Throws<PlinthException>(() => StoreUri.Open("azblob://tiles", NoEnv));
        Assert.Throws<PlinthException>(() => StoreUri.Open("s3://bucket", NoEnv));
        Assert.Throws<PlinthException>(() => StoreUri.Open("", NoEnv));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build errors — `AzureBlobStore`, `StoreUri` missing.

- [ ] **Step 4: Implement**

`src/Plinth.Pipeline/Stores/AzureBlobStore.cs`:

```csharp
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>One container, the store layout as blob names, immutable cache headers on every image.</summary>
public sealed class AzureBlobStore(BlobContainerClient container) : IOutputStore
{
    public const string CacheControl = "public, max-age=31536000, immutable";

    public static AzureBlobStore FromEnvironment(string containerName, Func<string, string?> env)
    {
        var conn = env("PLINTH_AZURE_STORAGE_CONNECTION");
        if (!string.IsNullOrEmpty(conn))
            return new AzureBlobStore(new BlobContainerClient(conn, containerName));
        var account = env("PLINTH_AZURE_STORAGE_ACCOUNT");
        if (!string.IsNullOrEmpty(account))
            return new AzureBlobStore(new BlobContainerClient(new Uri(new Uri(account), containerName), new DefaultAzureCredential()));
        throw new PlinthException("azblob store needs PLINTH_AZURE_STORAGE_CONNECTION or PLINTH_AZURE_STORAGE_ACCOUNT");
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        await container.GetBlobClient(StoreLayout.RecordPath(key)).ExistsAsync(ct);

    public async Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default)
    {
        var recordBlob = container.GetBlobClient(StoreLayout.RecordPath(key));
        ResultRecord record;
        try
        {
            var r = await recordBlob.DownloadContentAsync(ct);
            record = ResultRecord.FromJson(r.Value.Content.ToString());
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
        if (record.Output is null) return null;
        try
        {
            var img = await container.GetBlobClient(StoreLayout.ImagePath(key, record.Output.Format)).DownloadContentAsync(ct);
            return new StoredOutput(img.Value.Content.ToArray(), record);
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default)
    {
        StoreGuard.RequireStorable(record);
        var image = container.GetBlobClient(StoreLayout.ImagePath(key, record.Output!.Format));
        await image.UploadAsync(new BinaryData(bytes), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = ImageFormats.MimeTypeFor(record.Output.Format), CacheControl = CacheControl },
        }, ct);
        var json = container.GetBlobClient(StoreLayout.RecordPath(key));
        await json.UploadAsync(new BinaryData(record.ToJson()), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json", CacheControl = CacheControl },
        }, ct);
    }
}
```

`src/Plinth.Pipeline/Stores/StoreUri.cs`:

```csharp
using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>"none" | "fs://&lt;path&gt;" | "azblob://&lt;container&gt;" → a store.</summary>
public static class StoreUri
{
    public static IOutputStore Open(string uri, Func<string, string?> env)
    {
        if (string.IsNullOrWhiteSpace(uri)) throw new PlinthException("store URI is empty (use none, fs://<path> or azblob://<container>)");
        if (uri == "none") return new NullStore();
        if (uri.StartsWith("fs://", StringComparison.Ordinal))
        {
            var path = uri["fs://".Length..];
            if (path.Length == 0) throw new PlinthException("fs:// store needs a path");
            return new FileSystemStore(path);
        }
        if (uri.StartsWith("azblob://", StringComparison.Ordinal))
        {
            var containerName = uri["azblob://".Length..];
            if (containerName.Length == 0) throw new PlinthException("azblob:// store needs a container name");
            return AzureBlobStore.FromEnvironment(containerName, env);
        }
        throw new PlinthException($"unsupported store URI '{uri}'");
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass; the Azure round-trip shows as skipped unless `PLINTH_TEST_AZURE_CONNECTION` is set. If Azurite is installed locally (`npx azurite`), set the well-known dev connection string and run it once; report the result either way.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Azure Blob store and store URIs"
```

---

### Task 4: Fetch policy, private-address block and the HTTP fetcher

**Files:**
- Create: `src/Plinth.Pipeline/Fetch/FetchPolicy.cs`, `Fetch/PrivateAddress.cs`, `Fetch/ISourceFetcher.cs`, `Fetch/HttpSourceFetcher.cs`
- Test: `tests/Plinth.Tests/Fetch/PrivateAddressTests.cs`, `tests/Plinth.Tests/Fetch/HttpSourceFetcherTests.cs`, `tests/Plinth.Tests/Fakes/ScriptedHandler.cs`, `tests/Plinth.Tests/Fakes/FakeFetcher.cs`

**Interfaces (namespace `Plinth.Pipeline.Fetch`):**

```csharp
public sealed record FetchPolicy(
    IReadOnlySet<string> AllowedHosts,
    int MaxBytes = 12 * 1024 * 1024,
    int TimeoutSeconds = 12,
    int MaxRedirects = 3,
    string UserAgent = "Mozilla/5.0 (compatible; Plinth/1.0)")
{
    public static FetchPolicy FromHostList(string? commaList);   // null/empty → empty set
    public bool Allows(Uri uri);                                  // https + exact host match
}

public static class PrivateAddress { public static bool IsBlocked(IPAddress ip); }

public sealed record FetchResult(byte[] Bytes, string FinalUrl, string? ContentType);

public interface ISourceFetcher
{
    /// <summary>Fetch under the policy. Throws PlinthException for every refusal or failure.</summary>
    Task<FetchResult> FetchAsync(string url, CancellationToken ct = default);
}

public sealed class HttpSourceFetcher : ISourceFetcher
{
    public HttpSourceFetcher(FetchPolicy policy, HttpMessageHandler? handler = null);  // null → the guarded SocketsHttpHandler
}
```

- [ ] **Step 1: Write the fakes and the failing tests**

`tests/Plinth.Tests/Fakes/ScriptedHandler.cs`:

```csharp
using System.Net;

namespace Plinth.Tests.Fakes;

/// <summary>Answers requests from a script keyed by absolute URL; records what was asked.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _script = new();
    public List<string> Requested { get; } = [];

    public ScriptedHandler On(string url, Func<HttpResponseMessage> respond) { _script[url] = respond; return this; }

    public static HttpResponseMessage Bytes(byte[] body, string contentType = "image/jpeg", long? contentLength = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (contentLength is not null) r.Content.Headers.ContentLength = contentLength;
        return r;
    }

    public static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.Found)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return r;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);
        if (!_script.TryGetValue(url, out var respond))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(respond());
    }
}
```

`tests/Plinth.Tests/Fakes/FakeFetcher.cs`:

```csharp
using Plinth.Core;
using Plinth.Pipeline.Fetch;

namespace Plinth.Tests.Fakes;

/// <summary>Returns canned bytes per URL; counts calls; throws PlinthException for unknown URLs.</summary>
public sealed class FakeFetcher : ISourceFetcher
{
    private readonly Dictionary<string, byte[]> _bodies = new();
    public int Calls { get; private set; }

    public FakeFetcher With(string url, byte[] body) { _bodies[url] = body; return this; }

    public Task<FetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        Calls++;
        ct.ThrowIfCancellationRequested();
        if (!_bodies.TryGetValue(url, out var body)) throw new PlinthException("source 404");
        return Task.FromResult(new FetchResult(body, url, "image/jpeg"));
    }
}
```

`tests/Plinth.Tests/Fetch/PrivateAddressTests.cs`:

```csharp
using System.Net;
using Plinth.Pipeline.Fetch;

namespace Plinth.Tests.Fetch;

public class PrivateAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("104.18.1.22", false)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2606:4700::6810:116", false)]
    public void Blocks_private_loopback_link_local_and_metadata_ranges(string ip, bool blocked) =>
        Assert.Equal(blocked, PrivateAddress.IsBlocked(IPAddress.Parse(ip)));
}
```

`tests/Plinth.Tests/Fetch/HttpSourceFetcherTests.cs`:

```csharp
using System.Net;
using Plinth.Core;
using Plinth.Pipeline.Fetch;
using Plinth.Tests.Fakes;

namespace Plinth.Tests.Fetch;

public class HttpSourceFetcherTests
{
    private static readonly FetchPolicy Policy = FetchPolicy.FromHostList("img1.theiconic.com.au, cdn.example.com");
    private static readonly byte[] Body = [1, 2, 3, 4];

    private static HttpSourceFetcher Fetcher(ScriptedHandler h, FetchPolicy? p = null) => new(p ?? Policy, h);

    [Fact]
    public void Policy_parses_hosts_and_matches_exactly_over_https_only()
    {
        Assert.True(Policy.Allows(new Uri("https://cdn.example.com/a.jpg")));
        Assert.False(Policy.Allows(new Uri("http://cdn.example.com/a.jpg")));
        Assert.False(Policy.Allows(new Uri("https://sub.cdn.example.com/a.jpg")));
        Assert.False(Policy.Allows(new Uri("https://evil.com/cdn.example.com")));
        Assert.Empty(FetchPolicy.FromHostList(null).AllowedHosts);
    }

    [Fact]
    public async Task Allowlisted_source_is_fetched()
    {
        var h = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () => ScriptedHandler.Bytes(Body));
        var r = await Fetcher(h).FetchAsync("https://cdn.example.com/a.jpg");
        Assert.Equal(Body, r.Bytes);
        Assert.Equal("image/jpeg", r.ContentType);
        Assert.Single(h.Requested);
    }

    [Fact]
    public async Task Unlisted_host_or_http_is_refused_before_any_request()
    {
        var h = new ScriptedHandler();
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(h).FetchAsync("https://other.com/a.jpg"));
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(h).FetchAsync("http://cdn.example.com/a.jpg"));
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(h, FetchPolicy.FromHostList("")).FetchAsync("https://cdn.example.com/a.jpg"));
        Assert.Empty(h.Requested);
    }

    [Fact]
    public async Task Redirects_are_followed_only_within_the_allowlist_and_at_most_three_times()
    {
        var ok = new ScriptedHandler()
            .On("https://cdn.example.com/a", () => ScriptedHandler.Redirect("https://img1.theiconic.com.au/b"))
            .On("https://img1.theiconic.com.au/b", () => ScriptedHandler.Redirect("/c"))
            .On("https://img1.theiconic.com.au/c", () => ScriptedHandler.Bytes(Body));
        var r = await Fetcher(ok).FetchAsync("https://cdn.example.com/a");
        Assert.Equal("https://img1.theiconic.com.au/c", r.FinalUrl);

        var offList = new ScriptedHandler().On("https://cdn.example.com/a", () => ScriptedHandler.Redirect("https://evil.com/x"));
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(offList).FetchAsync("https://cdn.example.com/a"));
        Assert.Single(offList.Requested);

        var loop = new ScriptedHandler()
            .On("https://cdn.example.com/1", () => ScriptedHandler.Redirect("https://cdn.example.com/2"))
            .On("https://cdn.example.com/2", () => ScriptedHandler.Redirect("https://cdn.example.com/3"))
            .On("https://cdn.example.com/3", () => ScriptedHandler.Redirect("https://cdn.example.com/4"))
            .On("https://cdn.example.com/4", () => ScriptedHandler.Redirect("https://cdn.example.com/5"))
            .On("https://cdn.example.com/5", () => ScriptedHandler.Bytes(Body));
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(loop).FetchAsync("https://cdn.example.com/1"));
        Assert.Equal(4, loop.Requested.Count);
    }

    [Fact]
    public async Task Bodies_over_the_cap_are_refused_by_header_and_by_stream()
    {
        var small = new FetchPolicy(Policy.AllowedHosts, MaxBytes: 3);
        var byHeader = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () => ScriptedHandler.Bytes(Body, contentLength: 4));
        var ex = await Assert.ThrowsAsync<PlinthException>(() => Fetcher(byHeader, small).FetchAsync("https://cdn.example.com/a.jpg"));
        Assert.Contains("too large", ex.Message);

        var byStream = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () =>
        {
            var r = ScriptedHandler.Bytes(Body);
            r.Content.Headers.ContentLength = null;
            return r;
        });
        ex = await Assert.ThrowsAsync<PlinthException>(() => Fetcher(byStream, small).FetchAsync("https://cdn.example.com/a.jpg"));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public async Task Non_success_status_is_a_plinth_failure_with_the_status()
    {
        var h = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<PlinthException>(() => Fetcher(h).FetchAsync("https://cdn.example.com/a.jpg"));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Real_handler_refuses_a_host_that_resolves_privately()
    {
        // localhost is never on a real allowlist; put it on one to prove the connect-time block fires.
        var f = new HttpSourceFetcher(FetchPolicy.FromHostList("localhost"));
        var ex = await Assert.ThrowsAsync<PlinthException>(() => f.FetchAsync("https://localhost/x.jpg"));
        Assert.Contains("blocked address", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build errors — the `Plinth.Pipeline.Fetch` types do not exist.

- [ ] **Step 3: Implement**

`src/Plinth.Pipeline/Fetch/FetchPolicy.cs`:

```csharp
namespace Plinth.Pipeline.Fetch;

/// <summary>What the fetcher may touch. The allowlist IS the security model (spec 5.1).</summary>
public sealed record FetchPolicy(
    IReadOnlySet<string> AllowedHosts,
    int MaxBytes = 12 * 1024 * 1024,
    int TimeoutSeconds = 12,
    int MaxRedirects = 3,
    string UserAgent = "Mozilla/5.0 (compatible; Plinth/1.0)")
{
    public static FetchPolicy FromHostList(string? commaList) =>
        new(commaList is null
            ? new HashSet<string>()
            : commaList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => h.ToLowerInvariant()).ToHashSet());

    public bool Allows(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(uri.Host.ToLowerInvariant());
}
```

`src/Plinth.Pipeline/Fetch/PrivateAddress.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace Plinth.Pipeline.Fetch;

/// <summary>Addresses a public image fetcher must never connect to.</summary>
public static class PrivateAddress
{
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8
                || b[0] == 10                                  // 10/8
                || b[0] == 127                                 // loopback
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // 100.64/10 CGNAT
                || (b[0] == 169 && b[1] == 254)                 // link-local incl. 169.254.169.254
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168/16
                || b[0] >= 224;                                // multicast + reserved + broadcast
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(ip) || IPAddress.IPv6Any.Equals(ip)) return true;
            var b = ip.GetAddressBytes();
            return (b[0] & 0xfe) == 0xfc                      // fc00::/7 unique local
                || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80)     // fe80::/10 link-local
                || b[0] == 0xff;                               // multicast
        }
        return true;
    }
}
```

`src/Plinth.Pipeline/Fetch/ISourceFetcher.cs`:

```csharp
namespace Plinth.Pipeline.Fetch;

public sealed record FetchResult(byte[] Bytes, string FinalUrl, string? ContentType);

public interface ISourceFetcher
{
    /// <summary>Fetch under the policy. Throws PlinthException for every refusal or failure.</summary>
    Task<FetchResult> FetchAsync(string url, CancellationToken ct = default);
}
```

`src/Plinth.Pipeline/Fetch/HttpSourceFetcher.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Plinth.Core;

namespace Plinth.Pipeline.Fetch;

/// <summary>
/// The only thing in Plinth that opens a network connection. Redirects are
/// followed by hand so every hop is re-checked; the connect callback rejects
/// hosts that resolve to private addresses at connect time, which closes the
/// DNS-rebinding gap a pre-check would leave open.
/// </summary>
public sealed class HttpSourceFetcher : ISourceFetcher
{
    private readonly FetchPolicy _policy;
    private readonly HttpClient _http;

    public HttpSourceFetcher(FetchPolicy policy, HttpMessageHandler? handler = null)
    {
        _policy = policy;
        _http = new HttpClient(handler ?? GuardedHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(policy.TimeoutSeconds),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(policy.UserAgent);
    }

    private static SocketsHttpHandler GuardedHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            IPAddress[] addresses;
            try { addresses = await Dns.GetHostAddressesAsync(host, ct); }
            catch (SocketException e) { throw new PlinthException($"source host {host} did not resolve", e); }
            var allowed = addresses.Where(a => !PrivateAddress.IsBlocked(a)).ToArray();
            if (allowed.Length == 0) throw new PlinthException($"source host {host} resolves to a blocked address");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    public async Task<FetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !_policy.Allows(uri))
            throw new PlinthException($"source not allowed: {url}");

        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException e) when (e.InnerException is PlinthException pe) { throw pe; }
            catch (PlinthException) { throw; }
            catch (HttpRequestException e) { throw new PlinthException($"source fetch failed: {e.Message}", e); }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new PlinthException("source fetch timed out", e); }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= _policy.MaxRedirects) throw new PlinthException("source redirected too many times");
                    var location = response.Headers.Location ?? throw new PlinthException("source redirect without a location");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!_policy.Allows(next)) throw new PlinthException($"source redirected off the allowlist: {next}");
                    uri = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new PlinthException($"source {status}");

                var declared = response.Content.Headers.ContentLength;
                if (declared > _policy.MaxBytes) throw new PlinthException("source too large");

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new MemoryStream(capacity: (int)Math.Min(declared ?? 256 * 1024, _policy.MaxBytes));
                var chunk = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > _policy.MaxBytes) throw new PlinthException("source too large");
                    buffer.Write(chunk, 0, read);
                }
                return new FetchResult(buffer.ToArray(), uri.ToString(), response.Content.Headers.ContentType?.MediaType);
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass. The `Real_handler_refuses_a_host_that_resolves_privately` test exercises the actual `ConnectCallback`; if the exception surfaces wrapped differently (e.g. `HttpRequestException` whose inner is the `PlinthException` two levels down), adjust the unwrapping in `FetchAsync` to walk `InnerException` until a `PlinthException` is found, and say so in the report.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Fetch policy, private-address block and the guarded HTTP fetcher"
```

---

### Task 5: Signing, recipe catalog, options and the pipeline service

**Files:**
- Create: `src/Plinth.Pipeline/RequestSigning.cs`, `RecipeCatalog.cs`, `PipelineOptions.cs`, `PlinthPipeline.cs`
- Test: `tests/Plinth.Tests/RequestSigningTests.cs`, `RecipeCatalogTests.cs`, `PipelineOptionsTests.cs`, `PlinthPipelineTests.cs`

**Interfaces (namespace `Plinth.Pipeline`):**

```csharp
public static class RequestSigning
{
    public static string Sign(string src, string key);                 // lowercase hex HMAC-SHA256
    public static bool Verify(string src, string? sig, string key);    // constant-time
}

public sealed class RecipeCatalog
{
    public static RecipeCatalog DefaultOnly { get; }
    public static RecipeCatalog FromJson(string json);                 // {"name": {recipe fields}, ...}; "default" added if absent
    public Recipe Get(string? name);                                    // null/"" → default; unknown → PlinthException
    public IReadOnlyList<string> Names { get; }
}

public sealed record PipelineOptions(FetchPolicy Fetch, string StoreUri, RecipeCatalog Recipes, string? SigningKey, string OnFailure, int? Concurrency)
{
    public static PipelineOptions FromEnvironment(Func<string, string?> env);   // reads the PLINTH_* variables; PLINTH_RECIPES is a file path
}

public sealed record PipelineResult(string Status, byte[]? Bytes, ResultRecord Record, bool FromStore);   // Status: ok|passthrough|failed

public sealed class PlinthPipeline(ISourceFetcher fetcher, IOutputStore store, RecipeCatalog recipes)
{
    public RecipeCatalog Recipes { get; }
    public Task<PipelineResult> ProcessUrlAsync(string url, string? recipeName, CancellationToken ct = default);
    public Task<PipelineResult> ProcessBytesAsync(byte[] bytes, string? recipeName, string? sourceId, CancellationToken ct = default);
}
```

`ProcessUrlAsync`: `sourceId = SourceId.FromUrl(url)` (a `PlinthException` here yields a failed record with key computed from the raw url string as sourceId); `key = OutputKey.Compute(sourceId, recipe)`; `store.TryGetAsync(key)` hit → `PipelineResult(record.Status, bytes, record, FromStore: true)`; miss → fetch (failure → `ResultRecord.Failed`); `Normalizer.Normalize(bytes, recipe, sourceId, ct)`; if status is ok or passthrough → `store.PutAsync`. `ProcessBytesAsync`: same without the fetch; `sourceId ?? SourceId.FromBytes`.

- [ ] **Step 1: Write the failing tests**

`tests/Plinth.Tests/RequestSigningTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Plinth.Pipeline;

namespace Plinth.Tests;

public class RequestSigningTests
{
    [Fact]
    public void Sign_is_hmac_sha256_hex_and_verify_is_exact()
    {
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData("k"u8.ToArray(), Encoding.UTF8.GetBytes("https://a/b.jpg")));
        var sig = RequestSigning.Sign("https://a/b.jpg", "k");
        Assert.Equal(expected, sig);
        Assert.True(RequestSigning.Verify("https://a/b.jpg", sig, "k"));
        Assert.True(RequestSigning.Verify("https://a/b.jpg", sig.ToUpperInvariant(), "k"));
        Assert.False(RequestSigning.Verify("https://a/b.jpg", sig, "other"));
        Assert.False(RequestSigning.Verify("https://a/c.jpg", sig, "k"));
        Assert.False(RequestSigning.Verify("https://a/b.jpg", null, "k"));
        Assert.False(RequestSigning.Verify("https://a/b.jpg", "zz", "k"));
    }
}
```

`tests/Plinth.Tests/RecipeCatalogTests.cs`:

```csharp
using Plinth.Core;
using Plinth.Pipeline;

namespace Plinth.Tests;

public class RecipeCatalogTests
{
    [Fact]
    public void Default_is_always_present_and_names_resolve()
    {
        var c = RecipeCatalog.FromJson("{\"square\":{\"aspect\":\"1:1\",\"width\":800}}");
        Assert.Equal(["default", "square"], c.Names);
        Assert.Equal(Recipe.Default.Hash, c.Get(null).Hash);
        Assert.Equal(Recipe.Default.Hash, c.Get("").Hash);
        Assert.Equal(800, c.Get("square").CanvasHeight);
        Assert.Throws<PlinthException>(() => c.Get("nope"));
        Assert.Throws<PlinthException>(() => RecipeCatalog.FromJson("[1]"));
        Assert.Throws<PlinthException>(() => RecipeCatalog.FromJson("{\"bad\":{\"format\":\"gif\"}}"));
    }
}
```

`tests/Plinth.Tests/PipelineOptionsTests.cs`:

```csharp
using Plinth.Core;
using Plinth.Pipeline;

namespace Plinth.Tests;

public class PipelineOptionsTests
{
    [Fact]
    public void Reads_every_variable_with_sane_defaults()
    {
        var recipes = Path.GetTempFileName();
        File.WriteAllText(recipes, "{\"tall\":{\"aspect\":\"2:3\"}}");
        var env = new Dictionary<string, string?>
        {
            ["PLINTH_ALLOWED_HOSTS"] = "a.com, B.com",
            ["PLINTH_STORE"] = "fs:///tmp/x",
            ["PLINTH_RECIPES"] = recipes,
            ["PLINTH_SIGNING_KEY"] = "secret",
            ["PLINTH_ON_FAILURE"] = "error",
            ["PLINTH_CONCURRENCY"] = "2",
        };
        var o = PipelineOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(new HashSet<string> { "a.com", "b.com" }, o.Fetch.AllowedHosts);
        Assert.Equal("fs:///tmp/x", o.StoreUri);
        Assert.Equal(["default", "tall"], o.Recipes.Names);
        Assert.Equal("secret", o.SigningKey);
        Assert.Equal("error", o.OnFailure);
        Assert.Equal(2, o.Concurrency);

        var bare = PipelineOptions.FromEnvironment(_ => null);
        Assert.Empty(bare.Fetch.AllowedHosts);
        Assert.Equal("none", bare.StoreUri);
        Assert.Equal(["default"], bare.Recipes.Names);
        Assert.Null(bare.SigningKey);
        Assert.Equal("redirect", bare.OnFailure);
        Assert.Null(bare.Concurrency);

        Assert.Throws<PlinthException>(() => PipelineOptions.FromEnvironment(k => k == "PLINTH_ON_FAILURE" ? "explode" : null));
    }
}
```

`tests/Plinth.Tests/PlinthPipelineTests.cs`:

```csharp
using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Stores;
using Plinth.Tests.Fakes;

namespace Plinth.Tests;

public class PlinthPipelineTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private static byte[] Shot() => Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black);
    private const string Url = "https://cdn.example.com/a.jpg";

    [Fact]
    public async Task First_call_fetches_and_stores_second_call_is_served_from_the_store()
    {
        var fetcher = new FakeFetcher().With(Url, Shot());
        var store = new MemoryStore();
        var p = new PlinthPipeline(fetcher, store, RecipeCatalog.DefaultOnly);

        var first = await p.ProcessUrlAsync(Url, null);
        Assert.Equal("ok", first.Status);
        Assert.False(first.FromStore);
        Assert.Equal(OutputKey.Compute(SourceId.FromUrl(Url), Recipe.Default), first.Record.Key);
        Assert.True(await store.ExistsAsync(first.Record.Key));

        var second = await p.ProcessUrlAsync(Url, null);
        Assert.True(second.FromStore);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task Fetch_failures_and_bad_urls_become_failed_records_and_are_not_stored()
    {
        var store = new MemoryStore();
        var p = new PlinthPipeline(new FakeFetcher(), store, RecipeCatalog.DefaultOnly);
        var r = await p.ProcessUrlAsync(Url, null);
        Assert.Equal("failed", r.Status);
        Assert.Null(r.Bytes);
        Assert.Contains("404", r.Record.Error);
        Assert.False(await store.ExistsAsync(r.Record.Key));

        var bad = await p.ProcessUrlAsync("http://not-https/a.jpg", null);
        Assert.Equal("failed", bad.Status);
    }

    [Fact]
    public async Task Bytes_path_keys_by_content_when_no_source_id_is_given()
    {
        var p = new PlinthPipeline(new FakeFetcher(), new MemoryStore(), RecipeCatalog.DefaultOnly);
        var bytes = Shot();
        var r = await p.ProcessBytesAsync(bytes, null, null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(OutputKey.Compute(SourceId.FromBytes(bytes), Recipe.Default), r.Record.Key);
    }

    [Fact]
    public async Task Unknown_recipe_is_a_failed_record_not_an_exception()
    {
        var p = new PlinthPipeline(new FakeFetcher().With(Url, Shot()), new MemoryStore(), RecipeCatalog.DefaultOnly);
        var r = await p.ProcessUrlAsync(Url, "nope");
        Assert.Equal("failed", r.Status);
        Assert.Contains("recipe", r.Record.Error);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build errors for the four new types.

- [ ] **Step 3: Implement**

`src/Plinth.Pipeline/RequestSigning.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Plinth.Pipeline;

/// <summary>Optional HMAC on the src parameter so a public endpoint cannot be used as a free normaliser.</summary>
public static class RequestSigning
{
    public static string Sign(string src, string key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(src)));

    public static bool Verify(string src, string? sig, string key)
    {
        if (string.IsNullOrEmpty(sig)) return false;
        byte[] given;
        try { given = Convert.FromHexString(sig); } catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(src));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
```

`src/Plinth.Pipeline/RecipeCatalog.cs`:

```csharp
using System.Text.Json;
using Plinth.Core;

namespace Plinth.Pipeline;

/// <summary>Named recipes. "default" is always present and always Recipe.Default unless overridden.</summary>
public sealed class RecipeCatalog
{
    private readonly SortedDictionary<string, Recipe> _recipes;

    private RecipeCatalog(SortedDictionary<string, Recipe> recipes) => _recipes = recipes;

    public static RecipeCatalog DefaultOnly { get; } = new(new SortedDictionary<string, Recipe>(StringComparer.Ordinal) { ["default"] = Recipe.Default });

    public static RecipeCatalog FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new PlinthException("recipes JSON must be an object of name → recipe");
        var map = new SortedDictionary<string, Recipe>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
            map[p.Name] = Recipe.FromJson(p.Value.GetRawText());
        map.TryAdd("default", Recipe.Default);
        return new RecipeCatalog(map);
    }

    public IReadOnlyList<string> Names => _recipes.Keys.ToList();

    public Recipe Get(string? name)
    {
        var n = string.IsNullOrEmpty(name) ? "default" : name;
        return _recipes.TryGetValue(n, out var r) ? r : throw new PlinthException($"unknown recipe '{n}'");
    }
}
```

`src/Plinth.Pipeline/PipelineOptions.cs`:

```csharp
using Plinth.Core;
using Plinth.Pipeline.Fetch;

namespace Plinth.Pipeline;

/// <summary>Everything the front doors read from the environment (spec 6.1).</summary>
public sealed record PipelineOptions(
    FetchPolicy Fetch,
    string StoreUri,
    RecipeCatalog Recipes,
    string? SigningKey,
    string OnFailure,
    int? Concurrency)
{
    public static PipelineOptions FromEnvironment(Func<string, string?> env)
    {
        var onFailure = env("PLINTH_ON_FAILURE") ?? "redirect";
        if (onFailure is not ("redirect" or "error")) throw new PlinthException("PLINTH_ON_FAILURE must be redirect or error");
        var recipesPath = env("PLINTH_RECIPES");
        var recipes = string.IsNullOrEmpty(recipesPath) ? RecipeCatalog.DefaultOnly : RecipeCatalog.FromJson(File.ReadAllText(recipesPath));
        int? concurrency = int.TryParse(env("PLINTH_CONCURRENCY"), out var c) && c > 0 ? c : null;
        var signing = env("PLINTH_SIGNING_KEY");
        return new PipelineOptions(
            FetchPolicy.FromHostList(env("PLINTH_ALLOWED_HOSTS")),
            env("PLINTH_STORE") ?? "none",
            recipes,
            string.IsNullOrEmpty(signing) ? null : signing,
            onFailure,
            concurrency);
    }
}
```

`src/Plinth.Pipeline/PlinthPipeline.cs`:

```csharp
using Plinth.Core;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;

namespace Plinth.Pipeline;

public sealed record PipelineResult(string Status, byte[]? Bytes, ResultRecord Record, bool FromStore);

/// <summary>Check the store by key, fetch, normalise, store. The one flow both front doors share.</summary>
public sealed class PlinthPipeline(ISourceFetcher fetcher, IOutputStore store, RecipeCatalog recipes)
{
    public RecipeCatalog Recipes { get; } = recipes;

    public async Task<PipelineResult> ProcessUrlAsync(string url, string? recipeName, CancellationToken ct = default)
    {
        Recipe recipe;
        try { recipe = Recipes.Get(recipeName); }
        catch (PlinthException e) { return Failed(url, url, Recipe.Default, e.Message); }

        string sourceId;
        try { sourceId = SourceId.FromUrl(url); }
        catch (PlinthException e) { return Failed(url, url, recipe, e.Message); }

        var key = OutputKey.Compute(sourceId, recipe);
        var cached = await store.TryGetAsync(key, ct);
        if (cached is not null) return new PipelineResult(cached.Record.Status, cached.Bytes, cached.Record, FromStore: true);

        byte[] bytes;
        try { bytes = (await fetcher.FetchAsync(url, ct)).Bytes; }
        catch (PlinthException e) { return new PipelineResult("failed", null, ResultRecord.Failed(key, sourceId, recipe, e.Message), false); }

        return await NormalizeAndStoreAsync(bytes, recipe, sourceId, ct);
    }

    public async Task<PipelineResult> ProcessBytesAsync(byte[] bytes, string? recipeName, string? sourceId, CancellationToken ct = default)
    {
        Recipe recipe;
        try { recipe = Recipes.Get(recipeName); }
        catch (PlinthException e) { return Failed(sourceId ?? SourceId.FromBytes(bytes), sourceId ?? SourceId.FromBytes(bytes), Recipe.Default, e.Message); }
        var id = sourceId ?? SourceId.FromBytes(bytes);
        var key = OutputKey.Compute(id, recipe);
        var cached = await store.TryGetAsync(key, ct);
        if (cached is not null) return new PipelineResult(cached.Record.Status, cached.Bytes, cached.Record, FromStore: true);
        return await NormalizeAndStoreAsync(bytes, recipe, id, ct);
    }

    private async Task<PipelineResult> NormalizeAndStoreAsync(byte[] bytes, Recipe recipe, string sourceId, CancellationToken ct)
    {
        var result = Normalizer.Normalize(bytes, recipe, sourceId, ct);
        if (result.Status is "ok" or "passthrough")
            await store.PutAsync(result.Record.Key, result.Output!, result.Record, ct);
        return new PipelineResult(result.Status, result.Output, result.Record, FromStore: false);
    }

    private static PipelineResult Failed(string sourceId, string keySource, Recipe recipe, string error) =>
        new("failed", null, ResultRecord.Failed(OutputKey.Compute(keySource, recipe), sourceId, recipe, error), false);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Request signing, recipe catalog, options and the shared pipeline"
```

---

### Task 6: The HTTP API

**Files:**
- Create: `src/Plinth.Api/Plinth.Api.csproj`, `src/Plinth.Api/ApiHost.cs`, `src/Plinth.Api/Program.cs`
- Modify: `Plinth.sln`, `Directory.Packages.props` (`Microsoft.AspNetCore.Mvc.Testing`), `tests/Plinth.Tests/Plinth.Tests.csproj`
- Test: `tests/Plinth.Tests/Api/ApiTests.cs`

**Interfaces:**
- Produces: `ApiHost.Build(string[] args, PipelineOptions? options = null, Action<WebApplicationBuilder>? configure = null)` → `WebApplication` with every route mapped; `Program.Main` = `ApiHost.Build(args).Run()`. Routes and headers per Global Constraints. Cache header on images: `Cache-Control: public, max-age=31536000, immutable`. `POST /v1/normalize` reads the raw body (max `Fetch.MaxBytes`), `?recipe=` optional, empty body → 400 JSON `{"error":"empty body"}`, failed → 422 with the record JSON. `GET /v1/inspect` returns the record JSON with 200 even when failed (it is a diagnostic). Missing `src` → 400. Bad signature → 403 `{"error":"bad signature"}` (only when a signing key is configured). `/version` → `{ "engineVersion", "libvipsVersion", "recipes": [...] }`.

- [ ] **Step 1: Create the project**

```bash
dotnet new web -n Plinth.Api -o src/Plinth.Api -f net10.0
dotnet sln add src/Plinth.Api/Plinth.Api.csproj
dotnet add src/Plinth.Api/Plinth.Api.csproj reference src/Plinth.Pipeline/Plinth.Pipeline.csproj
dotnet add tests/Plinth.Tests/Plinth.Tests.csproj reference src/Plinth.Api/Plinth.Api.csproj
```

Delete the template's `appsettings*.json` and `Properties/launchSettings.json`. `Plinth.Api.csproj` must be `Sdk="Microsoft.NET.Sdk.Web"` with only the Pipeline reference (remove any `Version` attributes the template wrote). Add `<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.11" />` to `Directory.Packages.props` and `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />` to the test csproj (it brings `Microsoft.AspNetCore.TestHost`). The test project also needs `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in an ItemGroup if the build complains about missing ASP.NET types.

- [ ] **Step 2: Write the failing tests**

`tests/Plinth.Tests/Api/ApiTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Plinth.Api;
using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;
using Plinth.Tests.Fakes;

namespace Plinth.Tests.Api;

public class ApiTests : IAsyncLifetime
{
    private const string Url = "https://cdn.example.com/a.jpg";
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private static byte[] Shot() => Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly FakeFetcher _fetcher = new FakeFetcher().With(Url, Shot());
    private readonly MemoryStore _store = new();

    private PipelineOptions Options(string? signingKey = null, string onFailure = "redirect") =>
        new(FetchPolicy.FromHostList("cdn.example.com"), "none", RecipeCatalog.DefaultOnly, signingKey, onFailure, 1);

    private async Task StartAsync(PipelineOptions options)
    {
        _app = ApiHost.Build([], options, b =>
        {
            b.WebHost.UseTestServer();
            b.Services.AddSingleton<ISourceFetcher>(_fetcher);
            b.Services.AddSingleton<IOutputStore>(_store);
        });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public Task InitializeAsync() => StartAsync(Options());
    public async Task DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }

    [Fact]
    public async Task Image_route_returns_the_normalised_tile_with_headers_and_caches_the_second_call()
    {
        var r = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/webp", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", r.Headers.CacheControl!.ToString());
        Assert.Equal("ok", r.Headers.GetValues("X-Plinth-Status").Single());
        Assert.Equal("miss", r.Headers.GetValues("X-Plinth-Cache").Single());
        Assert.Equal("true", r.Headers.GetValues("X-Plinth-Verdict").Single());
        Assert.Equal(64, r.Headers.GetValues("X-Plinth-Key").Single().Length);
        Assert.True((await r.Content.ReadAsByteArrayAsync()).Length > 1000);

        var again = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}");
        Assert.Equal("hit", again.Headers.GetValues("X-Plinth-Cache").Single());
        Assert.Equal(1, _fetcher.Calls);
    }

    [Fact]
    public async Task Failure_redirects_to_the_source_by_default_and_errors_when_configured()
    {
        var missing = "https://cdn.example.com/missing.jpg";
        var r = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(missing)}");
        Assert.Equal(HttpStatusCode.Found, r.StatusCode);
        Assert.Equal(missing, r.Headers.Location!.ToString());
        Assert.Equal("failed", r.Headers.GetValues("X-Plinth-Status").Single());

        await DisposeAsync();
        await StartAsync(Options(onFailure: "error"));
        r = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(missing)}");
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Signing_is_enforced_when_a_key_is_configured()
    {
        await DisposeAsync();
        await StartAsync(Options(signingKey: "k"));
        var unsigned = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}");
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
        var sig = RequestSigning.Sign(Url, "k");
        var signed = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}&sig={sig}");
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
    }

    [Fact]
    public async Task Missing_src_unknown_recipe_and_disallowed_host_are_client_errors_or_failures()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/v1/image")).StatusCode);
        var unknownRecipe = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}&recipe=nope");
        Assert.Equal(HttpStatusCode.Found, unknownRecipe.StatusCode);
        var off = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString("https://evil.com/a.jpg")}");
        Assert.Equal(HttpStatusCode.Found, off.StatusCode);
    }

    [Fact]
    public async Task Inspect_returns_the_record_and_normalize_accepts_raw_bytes()
    {
        var inspect = await _client.GetAsync($"/v1/inspect?src={Uri.EscapeDataString(Url)}");
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
        using var doc = JsonDocument.Parse(await inspect.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("verdict").GetProperty("packShot").GetBoolean());

        var post = await _client.PostAsync("/v1/normalize", new ByteArrayContent(Shot()));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("image/webp", post.Content.Headers.ContentType!.MediaType);
        Assert.Equal("ok", post.Headers.GetValues("X-Plinth-Status").Single());

        var empty = await _client.PostAsync("/v1/normalize", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var junk = await _client.PostAsync("/v1/normalize", new ByteArrayContent("nope"u8.ToArray()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, junk.StatusCode);
    }

    [Fact]
    public async Task Health_and_version()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/healthz")).StatusCode);
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/version"));
        Assert.Equal(Engine.Version, doc.RootElement.GetProperty("engineVersion").GetString());
        Assert.StartsWith("8.", doc.RootElement.GetProperty("libvipsVersion").GetString());
        Assert.Equal("default", doc.RootElement.GetProperty("recipes")[0].GetString());
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build errors — `Plinth.Api.ApiHost` missing.

- [ ] **Step 4: Implement**

`src/Plinth.Api/Program.cs`:

```csharp
using Plinth.Api;

ApiHost.Build(args).Run();
```

`src/Plinth.Api/ApiHost.cs`:

```csharp
using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;

namespace Plinth.Api;

/// <summary>Builds the API so tests and the CLI's `plinth api` can host it the same way.</summary>
public static class ApiHost
{
    public const string ImmutableCache = "public, max-age=31536000, immutable";

    public static WebApplication Build(string[] args, PipelineOptions? options = null, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        options ??= PipelineOptions.FromEnvironment(Environment.GetEnvironmentVariable);
        Engine.Init(options.Concurrency);

        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.Fetch.MaxBytes);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISourceFetcher>(_ => new HttpSourceFetcher(options.Fetch));
        builder.Services.AddSingleton<IOutputStore>(_ => StoreUri.Open(options.StoreUri, Environment.GetEnvironmentVariable));
        builder.Services.AddSingleton(sp => new PlinthPipeline(sp.GetRequiredService<ISourceFetcher>(), sp.GetRequiredService<IOutputStore>(), options.Recipes));
        configure?.Invoke(builder);

        var app = builder.Build();
        Map(app);
        return app;
    }

    private static void Map(WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/version", (PipelineOptions o) => Results.Ok(new
        {
            engineVersion = Engine.Version,
            libvipsVersion = Engine.LibvipsVersion,
            recipes = o.Recipes.Names,
        }));

        app.MapGet("/v1/image", async (string? src, string? recipe, string? sig, PlinthPipeline pipeline, PipelineOptions o, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(src)) return Results.BadRequest(new { error = "src is required" });
            if (o.SigningKey is not null && !RequestSigning.Verify(src, sig, o.SigningKey))
                return Results.Json(new { error = "bad signature" }, statusCode: StatusCodes.Status403Forbidden);

            var result = await pipeline.ProcessUrlAsync(src, recipe, ct);
            Stamp(http, result);
            if (result.Status == "failed")
                return o.OnFailure == "redirect"
                    ? Results.Redirect(src, permanent: false)
                    : Results.Text(result.Record.ToJson(), "application/json", statusCode: StatusCodes.Status502BadGateway);

            http.Response.Headers.CacheControl = ImmutableCache;
            return Results.Bytes(result.Bytes!, ImageFormats.MimeTypeFor(result.Record.Output!.Format));
        });

        app.MapGet("/v1/inspect", async (string? src, string? recipe, string? sig, PlinthPipeline pipeline, PipelineOptions o, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(src)) return Results.BadRequest(new { error = "src is required" });
            if (o.SigningKey is not null && !RequestSigning.Verify(src, sig, o.SigningKey))
                return Results.Json(new { error = "bad signature" }, statusCode: StatusCodes.Status403Forbidden);
            var result = await pipeline.ProcessUrlAsync(src, recipe, ct);
            return Results.Text(result.Record.ToJson(), "application/json");
        });

        app.MapPost("/v1/normalize", async (string? recipe, PlinthPipeline pipeline, HttpContext http, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length == 0) return Results.BadRequest(new { error = "empty body" });
            var result = await pipeline.ProcessBytesAsync(ms.ToArray(), recipe, null, ct);
            Stamp(http, result);
            if (result.Status == "failed")
                return Results.Text(result.Record.ToJson(), "application/json", statusCode: StatusCodes.Status422UnprocessableEntity);
            return Results.Bytes(result.Bytes!, ImageFormats.MimeTypeFor(result.Record.Output!.Format));
        });
    }

    private static void Stamp(HttpContext http, PipelineResult r)
    {
        var h = http.Response.Headers;
        h["X-Plinth-Key"] = r.Record.Key;
        h["X-Plinth-Status"] = r.Status;
        h["X-Plinth-Cache"] = r.FromStore ? "hit" : "miss";
        h["X-Plinth-Verdict"] = r.Record.Verdict.PackShot ? "true" : "false";
        h["X-Plinth-Confidence"] = r.Record.Verdict.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass. If `Results.Bytes` sets its own cache headers or the test server drops `Cache-Control`, read the header from `r.Headers` vs `r.Content.Headers` accordingly and note it.

- [ ] **Step 6: Run it for real once**

```bash
PLINTH_ALLOWED_HOSTS=img1.theiconic.com.au dotnet run --project src/Plinth.Api --urls http://127.0.0.1:5088 &
sleep 4; curl -s http://127.0.0.1:5088/version; echo
curl -s -o /tmp/tile.webp -D - "http://127.0.0.1:5088/v1/image?src=$(python3 -c 'import urllib.parse,sys;print(urllib.parse.quote(open("tests/fixtures/sources.txt").read().split("\n")[0].split("\t")[1]))')" | head -12
kill %1
```

Pick a line from `tests/fixtures/sources.txt` whose host is `img1.theiconic.com.au` (the quoted python takes the first line; adjust the index if the first is another host, and add that host to the allowlist instead). Expected: `200`, `content-type: image/webp`, the `X-Plinth-*` headers, and a file `/tmp/tile.webp` you can open. Put the headers in the report.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "The HTTP API: image, inspect, normalize, health and version"
```

---

### Task 7: The CLI

**Files:**
- Create: `src/Plinth.Cli/Plinth.Cli.csproj`, `src/Plinth.Cli/CliApp.cs`, `src/Plinth.Cli/RunCommand.cs`, `src/Plinth.Cli/Program.cs`
- Modify: `Plinth.sln`, `Directory.Packages.props` (`System.CommandLine` 2.0.11), `tests/Plinth.Tests/Plinth.Tests.csproj`
- Test: `tests/Plinth.Tests/Cli/CliTests.cs`

**Interfaces:**
- Produces: `CliApp.Build(Func<PipelineOptions, ISourceFetcher>? fetcherFactory = null, TextWriter? stdout = null)` → `RootCommand` (the factory lets tests inject `FakeFetcher`); `Program.Main` = `await CliApp.Build().Parse(args).InvokeAsync()`. Commands per Global Constraints. `run` semantics:
  - `--input` is a directory (every file, sorted), a file of URLs one per line (`-` = stdin), or a single image path. URL lists are sorted by host so connections stay warm.
  - `--output` is a directory (→ `fs://`) or a store URI (`azblob://...`, `none`).
  - `--recipe` is a catalog name (from `PLINTH_RECIPES`) or a path to a recipe JSON file.
  - Skip: for a URL, `store.ExistsAsync(key)` before fetching unless `--refresh`; with `--refresh` the source is refetched, its sha256 compared with the stored record's `source.sha256`, and only a changed source is re-normalised. For a local file the bytes are read (cheap), keyed by content, and skipped if the key exists.
  - Two stages joined by a bounded channel: 8 fetch workers (I/O) and `--concurrency` process workers (CPU, default `Environment.ProcessorCount`); `Engine.Init(1)` so libvips does not multiply the thread count (spec 12.3).
  - Manifest: one JSON line per item, `{"key","sourceId","status","error","outputBytes"}` where status is `ok|passthrough|failed|skipped|unchanged`; written to `--manifest` or stdout when omitted. A summary line goes to stderr: `N items: a ok, b passthrough, c failed, d skipped, e unchanged`.
  - Exit code 0 unless the run itself broke (unreadable input, bad store URI, bad recipe) → 2 with the message on stderr.

- [ ] **Step 1: Create the project**

```bash
dotnet new console -n Plinth.Cli -o src/Plinth.Cli -f net10.0
dotnet sln add src/Plinth.Cli/Plinth.Cli.csproj
dotnet add src/Plinth.Cli/Plinth.Cli.csproj reference src/Plinth.Pipeline/Plinth.Pipeline.csproj src/Plinth.Api/Plinth.Api.csproj
dotnet add tests/Plinth.Tests/Plinth.Tests.csproj reference src/Plinth.Cli/Plinth.Cli.csproj
```

Add `<PackageVersion Include="System.CommandLine" Version="2.0.11" />` to `Directory.Packages.props`; in `Plinth.Cli.csproj` add `<PackageReference Include="System.CommandLine" />`, both NetVips native packages (`osx-arm64`, `linux-x64`) as PackageReferences, and `<AssemblyName>plinth</AssemblyName>` so the binary is `plinth`. Remove `Version` attributes the CLI wrote.

- [ ] **Step 2: Write the failing tests**

`tests/Plinth.Tests/Cli/CliTests.cs`:

```csharp
using System.CommandLine;
using System.Text.Json;
using Plinth.Cli;
using Plinth.Core;
using Plinth.Pipeline.Stores;
using Plinth.Tests.Fakes;

namespace Plinth.Tests.Cli;

public class CliTests : IDisposable
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plinth-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string _out;

    public CliTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "in"));
        _out = Path.Combine(_dir, "out");
        for (var i = 0; i < 3; i++)
            File.WriteAllBytes(Path.Combine(_dir, "in", $"p{i}.jpg"), Synthetic.PackShot(600 + i * 10, 800, White, 100, 100, 200, 300, Black));
        File.WriteAllBytes(Path.Combine(_dir, "in", "junk.jpg"), "not an image"u8.ToArray());
    }

    private static async Task<(int Code, List<JsonElement> Lines)> Run(RootCommand root, params string[] args)
    {
        var manifest = Path.Combine(Path.GetTempPath(), "plinth-manifest-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var code = await root.Parse([.. args, "--manifest", manifest]).InvokeAsync();
        var lines = File.Exists(manifest)
            ? File.ReadAllLines(manifest).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList()
            : [];
        return (code, lines);
    }

    private static int Count(List<JsonElement> lines, string status) => lines.Count(l => l.GetProperty("status").GetString() == status);

    [Fact]
    public async Task Run_over_a_directory_normalises_once_and_skips_on_rerun()
    {
        var root = CliApp.Build();
        var first = await Run(root, "run", "--input", Path.Combine(_dir, "in"), "--output", _out, "--concurrency", "2");
        Assert.Equal(0, first.Code);
        Assert.Equal(4, first.Lines.Count);
        Assert.Equal(3, Count(first.Lines, "ok"));
        Assert.Equal(1, Count(first.Lines, "failed"));
        Assert.Equal(3, Directory.GetFiles(_out, "*.webp", SearchOption.AllDirectories).Length);

        var second = await Run(root, "run", "--input", Path.Combine(_dir, "in"), "--output", _out);
        Assert.Equal(0, second.Code);
        Assert.Equal(3, Count(second.Lines, "skipped"));
        Assert.Equal(1, Count(second.Lines, "failed"));
    }

    [Fact]
    public async Task Run_over_a_url_list_skips_by_key_without_fetching_and_refresh_refetches()
    {
        var fetcher = new FakeFetcher()
            .With("https://cdn.example.com/a.jpg", Synthetic.PackShot(600, 800, White, 100, 100, 200, 300, Black))
            .With("https://cdn.example.com/b.jpg", Synthetic.PackShot(640, 800, White, 100, 100, 200, 300, Black));
        var list = Path.Combine(_dir, "urls.txt");
        File.WriteAllLines(list, ["https://cdn.example.com/b.jpg", "https://cdn.example.com/a.jpg", "https://cdn.example.com/missing.jpg"]);
        Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", "cdn.example.com");
        try
        {
            var root = CliApp.Build(_ => fetcher);
            var first = await Run(root, "run", "--input", list, "--output", _out);
            Assert.Equal(0, first.Code);
            Assert.Equal(2, Count(first.Lines, "ok"));
            Assert.Equal(1, Count(first.Lines, "failed"));
            Assert.Equal(3, fetcher.Calls);

            var second = await Run(root, "run", "--input", list, "--output", _out);
            Assert.Equal(2, Count(second.Lines, "skipped"));
            Assert.Equal(4, fetcher.Calls);   // only the failed one is retried

            var refreshed = await Run(root, "run", "--input", list, "--output", _out, "--refresh");
            Assert.Equal(2, Count(refreshed.Lines, "unchanged"));
            Assert.Equal(7, fetcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable("PLINTH_ALLOWED_HOSTS", null); }
    }

    [Fact]
    public async Task Key_inspect_and_version_print_and_bad_usage_exits_two()
    {
        var stdout = new StringWriter();
        var root = CliApp.Build(stdout: stdout);
        var file = Path.Combine(_dir, "in", "p0.jpg");
        Assert.Equal(0, await root.Parse(["key", file]).InvokeAsync());
        Assert.Equal(64, stdout.ToString().Trim().Length);

        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await root.Parse(["inspect", file]).InvokeAsync());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());

        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await root.Parse(["version"]).InvokeAsync());
        Assert.Contains(Engine.Version, stdout.ToString());

        Assert.Equal(2, await root.Parse(["run", "--input", Path.Combine(_dir, "nope"), "--output", _out]).InvokeAsync());
        Assert.NotEqual(0, await root.Parse(["run"]).InvokeAsync());
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build errors — `Plinth.Cli.CliApp` missing.

- [ ] **Step 4: Implement**

`src/Plinth.Cli/Program.cs`:

```csharp
using System.CommandLine;
using Plinth.Cli;

return await CliApp.Build().Parse(args).InvokeAsync();
```

`src/Plinth.Cli/CliApp.cs`:

```csharp
using System.CommandLine;
using Plinth.Api;
using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Fetch;

namespace Plinth.Cli;

/// <summary>The command tree. Built by a function so tests can inject a fetcher and capture stdout.</summary>
public static class CliApp
{
    public static RootCommand Build(Func<PipelineOptions, ISourceFetcher>? fetcherFactory = null, TextWriter? stdout = null)
    {
        var outWriter = stdout ?? Console.Out;
        var fetchers = fetcherFactory ?? (o => new HttpSourceFetcher(o.Fetch));
        var root = new RootCommand("Plinth puts every product on the same plinth.");

        root.Subcommands.Add(RunCommand.Build(fetchers, outWriter));

        var pathArg = new Argument<string>("file-or-url") { Description = "An image file or an https URL on the allowlist" };
        var recipeOpt = new Option<string?>("--recipe") { Description = "Recipe name from PLINTH_RECIPES, or a recipe JSON file" };

        var key = new Command("key", "Print the output key for a source") { pathArg, recipeOpt };
        key.SetAction(async (pr, ct) =>
        {
            var options = PipelineOptions.FromEnvironment(Environment.GetEnvironmentVariable);
            var recipe = RunCommand.ResolveRecipe(pr.GetValue(recipeOpt), options.Recipes);
            var target = pr.GetValue(pathArg)!;
            var id = target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? SourceId.FromUrl(target)
                : SourceId.FromBytes(await File.ReadAllBytesAsync(target, ct));
            outWriter.WriteLine(OutputKey.Compute(id, recipe));
            return 0;
        });
        root.Subcommands.Add(key);

        var inspect = new Command("inspect", "Normalise a source and print its result record") { pathArg, recipeOpt };
        inspect.SetAction(async (pr, ct) =>
        {
            var options = PipelineOptions.FromEnvironment(Environment.GetEnvironmentVariable);
            Engine.Init(options.Concurrency);
            var pipeline = new PlinthPipeline(fetchers(options), new Pipeline.Stores.NullStore(), options.Recipes);
            var recipeName = pr.GetValue(recipeOpt);
            var target = pr.GetValue(pathArg)!;
            var result = target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? await pipeline.ProcessUrlAsync(target, recipeName, ct)
                : await pipeline.ProcessBytesAsync(await File.ReadAllBytesAsync(target, ct), recipeName, null, ct);
            outWriter.WriteLine(result.Record.ToJson());
            return 0;
        });
        root.Subcommands.Add(inspect);

        var version = new Command("version", "Print engine and libvips versions");
        version.SetAction(_ => { outWriter.WriteLine($"plinth engine {Engine.Version} libvips {Engine.LibvipsVersion}"); return 0; });
        root.Subcommands.Add(version);

        var api = new Command("api", "Run the HTTP API (reads PLINTH_* from the environment)");
        api.SetAction(async (_, ct) => { await ApiHost.Build([]).RunAsync(ct); return 0; });
        root.Subcommands.Add(api);

        return root;
    }
}
```

Note: `inspect` with a named recipe uses the catalog; a recipe *file* path is handled by `RunCommand.ResolveRecipe` for `key` and `run` only (inspect goes through the pipeline's catalog). That is acceptable: document it in the README.

`src/Plinth.Cli/RunCommand.cs`:

```csharp
using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;

namespace Plinth.Cli;

/// <summary>`plinth run`: fetch stage → process stage, skip by key, one manifest line per item.</summary>
public static class RunCommand
{
    private const int FetchWorkers = 8;

    private sealed record Item(string Display, string? Url, string? Path);
    private sealed record Fetched(Item Item, string SourceId, string Key, byte[]? Bytes, string? Skip, string? Error);
    private sealed record ManifestLine(string Key, string SourceId, string Status, string? Error, int? OutputBytes);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Command Build(Func<PipelineOptions, ISourceFetcher> fetchers, TextWriter stdout)
    {
        var input = new Option<string>("--input", "-i") { Description = "Directory of images, a file of URLs (- for stdin), or one image", Required = true };
        var output = new Option<string>("--output", "-o") { Description = "Output directory, or a store URI (azblob://container, none)", Required = true };
        var recipe = new Option<string?>("--recipe") { Description = "Recipe name from PLINTH_RECIPES, or a recipe JSON file" };
        var concurrency = new Option<int>("--concurrency") { Description = "Process workers", DefaultValueFactory = _ => Environment.ProcessorCount };
        var manifest = new Option<string?>("--manifest") { Description = "Write one JSON line per item here (default stdout)" };
        var refresh = new Option<bool>("--refresh") { Description = "Refetch sources whose key exists and reprocess only if the bytes changed" };

        var cmd = new Command("run", "Normalise a batch of sources into a store") { input, output, recipe, concurrency, manifest, refresh };
        cmd.SetAction(async (pr, ct) =>
        {
            try
            {
                var options = PipelineOptions.FromEnvironment(Environment.GetEnvironmentVariable);
                var rec = ResolveRecipe(pr.GetValue(recipe), options.Recipes);
                var store = OpenOutput(pr.GetValue(output)!);
                var items = ReadItems(pr.GetValue(input)!);
                Engine.Init(1);
                var workers = Math.Max(1, pr.GetValue(concurrency));
                await using var manifestWriter = OpenManifest(pr.GetValue(manifest), stdout);
                var counts = await RunAsync(items, rec, store, fetchers(options), workers, pr.GetValue(refresh), manifestWriter, ct);
                Console.Error.WriteLine($"{items.Count} items: {counts.GetValueOrDefault("ok")} ok, {counts.GetValueOrDefault("passthrough")} passthrough, {counts.GetValueOrDefault("failed")} failed, {counts.GetValueOrDefault("skipped")} skipped, {counts.GetValueOrDefault("unchanged")} unchanged");
                return 0;
            }
            catch (Exception e) when (e is PlinthException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"plinth run: {e.Message}");
                return 2;
            }
        });
        return cmd;
    }

    public static Recipe ResolveRecipe(string? nameOrPath, RecipeCatalog catalog)
    {
        if (nameOrPath is not null && File.Exists(nameOrPath)) return Recipe.FromJson(File.ReadAllText(nameOrPath));
        return catalog.Get(nameOrPath);
    }

    private static IOutputStore OpenOutput(string output) =>
        output == "none" || output.Contains("://", StringComparison.Ordinal)
            ? StoreUri.Open(output, Environment.GetEnvironmentVariable)
            : new FileSystemStore(output);

    private static List<Item> ReadItems(string input)
    {
        if (input == "-")
            return UrlItems(Console.In.ReadToEnd().Split('\n'));
        if (Directory.Exists(input))
            return Directory.EnumerateFiles(input).Order(StringComparer.Ordinal).Select(f => new Item(f, null, f)).ToList();
        if (!File.Exists(input)) throw new PlinthException($"input not found: {input}");
        var head = File.ReadAllText(input).TrimStart();
        if (head.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return UrlItems(File.ReadAllLines(input));
        return [new Item(input, null, input)];
    }

    private static List<Item> UrlItems(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(u => new Item(u, u, null))
            .OrderBy(i => Uri.TryCreate(i.Url, UriKind.Absolute, out var x) ? x.Host : "", StringComparer.Ordinal)
            .ThenBy(i => i.Url, StringComparer.Ordinal)
            .ToList();

    private static StreamWriter OpenManifest(string? path, TextWriter stdout) =>
        path is null ? new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true } : new StreamWriter(path, append: false);

    private static async Task<Dictionary<string, int>> RunAsync(List<Item> items, Recipe recipe, IOutputStore store, ISourceFetcher fetcher,
        int workers, bool refresh, StreamWriter manifest, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>();
        var manifestLock = new object();

        void Emit(ManifestLine line)
        {
            lock (manifestLock)
            {
                manifest.WriteLine(JsonSerializer.Serialize(line, Json));
                counts[line.Status] = counts.GetValueOrDefault(line.Status) + 1;
            }
        }

        var fetched = Channel.CreateBounded<Fetched>(new BoundedChannelOptions(workers * 2) { SingleWriter = false });
        var todo = Channel.CreateUnbounded<Item>();
        foreach (var i in items) todo.Writer.TryWrite(i);
        todo.Writer.Complete();

        var fetchStage = Enumerable.Range(0, FetchWorkers).Select(_ => Task.Run(async () =>
        {
            await foreach (var item in todo.Reader.ReadAllAsync(ct))
                await fetched.Writer.WriteAsync(await FetchOneAsync(item, recipe, store, fetcher, refresh, ct), ct);
        }, ct)).ToArray();
        var closer = Task.WhenAll(fetchStage).ContinueWith(_ => fetched.Writer.Complete(), ct);

        await Parallel.ForEachAsync(fetched.Reader.ReadAllAsync(ct), new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, async (f, token) =>
        {
            if (f.Skip is not null) { Emit(new ManifestLine(f.Key, f.SourceId, f.Skip, null, null)); return; }
            if (f.Error is not null) { Emit(new ManifestLine(f.Key, f.SourceId, "failed", f.Error, null)); return; }
            var result = Normalizer.Normalize(f.Bytes!, recipe, f.SourceId, token);
            if (result.Status is "ok" or "passthrough")
                await store.PutAsync(result.Record.Key, result.Output!, result.Record, token);
            Emit(new ManifestLine(result.Record.Key, f.SourceId, result.Status, result.Record.Error, result.Output?.Length));
        });
        await closer;
        return counts;
    }

    private static async Task<Fetched> FetchOneAsync(Item item, Recipe recipe, IOutputStore store, ISourceFetcher fetcher, bool refresh, CancellationToken ct)
    {
        if (item.Path is not null)
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(item.Path, ct); }
            catch (IOException e) { return new Fetched(item, item.Path, OutputKey.Compute(item.Path, recipe), null, null, e.Message); }
            var id = SourceId.FromBytes(bytes);
            var key = OutputKey.Compute(id, recipe);
            if (!refresh && await store.ExistsAsync(key, ct)) return new Fetched(item, id, key, null, "skipped", null);
            return new Fetched(item, id, key, bytes, null, null);
        }

        string sourceId;
        try { sourceId = SourceId.FromUrl(item.Url!); }
        catch (PlinthException e) { return new Fetched(item, item.Url!, OutputKey.Compute(item.Url!, recipe), null, null, e.Message); }
        var urlKey = OutputKey.Compute(sourceId, recipe);
        if (!refresh && await store.ExistsAsync(urlKey, ct)) return new Fetched(item, sourceId, urlKey, null, "skipped", null);

        byte[] body;
        try { body = (await fetcher.FetchAsync(item.Url!, ct)).Bytes; }
        catch (PlinthException e) { return new Fetched(item, sourceId, urlKey, null, null, e.Message); }

        if (refresh)
        {
            var stored = await store.TryGetAsync(urlKey, ct);
            if (stored is not null && stored.Record.Source.Sha256 == Convert.ToHexStringLower(SHA256.HashData(body)))
                return new Fetched(item, sourceId, urlKey, null, "unchanged", null);
        }
        return new Fetched(item, sourceId, urlKey, body, null, null);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all pass. Two things to check if they don't: `Option<T>` in System.CommandLine 2.0.11 takes `(string name, params string[] aliases)` and exposes `Required` and `DefaultValueFactory` — confirm against `~/.nuget/packages/system.commandline/2.0.11/lib/net8.0/System.CommandLine.xml` if the compiler disagrees; and a `manifest` writer over stdout must not be disposed in a way that closes the process's stdout (the `await using` only closes the wrapper; if the second test in a process then loses output, wrap `Console.OpenStandardOutput()` in a non-closing stream).

- [ ] **Step 6: Try the binary once**

```bash
dotnet run --project src/Plinth.Cli -- version
dotnet run --project src/Plinth.Cli -- run --input tests/fixtures/src --output /tmp/plinth-out --manifest /tmp/plinth.jsonl && wc -l /tmp/plinth.jsonl && find /tmp/plinth-out -name '*.webp' | wc -l
```

Expected: `plinth engine 1.0 libvips 8.18.6`; 13 manifest lines; 13 webp files. Put the stderr summary line in the report.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "The CLI: run, inspect, key, version and api"
```

---

### Task 8: Docker image

**Files:**
- Create: `Dockerfile`, `.dockerignore`
- Modify: `Directory.Packages.props`, `src/Plinth.Cli/Plinth.Cli.csproj`, `tests/Plinth.Tests/Plinth.Tests.csproj`, `src/Plinth.Bench/Plinth.Bench.csproj` (add `NetVips.Native.linux-arm64` if it exists)

- [ ] **Step 1: Check the arm64 native package exists**

```bash
curl -s https://api.nuget.org/v3-flatcontainer/netvips.native.linux-arm64/index.json | python3 -c 'import json,sys; v=json.load(sys.stdin)["versions"]; print("8.18.6" in v)'
```

If `True`: add `<PackageVersion Include="NetVips.Native.linux-arm64" Version="8.18.6" />` and a `PackageReference` in Cli, Bench and Tests. If `False`: skip and build the image for `linux/amd64` only (add `--platform linux/amd64` to every docker command below and note it in the README).

- [ ] **Step 2: Write the Dockerfile and .dockerignore**

`.dockerignore`:

```
**/bin/
**/obj/
.git/
.superpowers/
docs/bench/latest.json
tests/fixtures/src/
```

`Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props Plinth.sln ./
COPY src/Plinth.Core/Plinth.Core.csproj src/Plinth.Core/
COPY src/Plinth.Pipeline/Plinth.Pipeline.csproj src/Plinth.Pipeline/
COPY src/Plinth.Api/Plinth.Api.csproj src/Plinth.Api/
COPY src/Plinth.Cli/Plinth.Cli.csproj src/Plinth.Cli/
RUN dotnet restore src/Plinth.Cli/Plinth.Cli.csproj
COPY src/ src/
RUN dotnet publish src/Plinth.Cli/Plinth.Cli.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    PLINTH_STORE=none
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "plinth.dll"]
CMD ["version"]
```

- [ ] **Step 3: Build and smoke-test**

```bash
docker build -t plinth:dev .
docker run --rm plinth:dev
docker run --rm -v "$PWD/tests/fixtures/src:/in:ro" -v /tmp/plinth-docker:/out plinth:dev run --input /in --output /out --manifest /out/manifest.jsonl; wc -l /tmp/plinth-docker/manifest.jsonl
docker run --rm -d -p 8080:8080 -e PLINTH_ALLOWED_HOSTS=img1.theiconic.com.au --name plinth-smoke plinth:dev api; sleep 3; curl -s localhost:8080/healthz; curl -s localhost:8080/version; docker rm -f plinth-smoke
```

Expected: `plinth engine 1.0 libvips 8.18.6` from the first run; 13 manifest lines from the batch; `{"status":"ok"}` and the version JSON from the API. If `USER $APP_UID` cannot write `/out`, run the batch container with `--user $(id -u)` and say so in the README's Docker section. Record the image size (`docker images plinth:dev`).

- [ ] **Step 4: Run the tests and commit**

```bash
dotnet test && git add -A && git commit -m "One Docker image that runs the CLI and the API"
```

---

### Task 9: CI and release workflows

**Files:**
- Create: `.github/workflows/ci.yml`, `.github/workflows/release.yml`

- [ ] **Step 1: Write the workflows**

`.github/workflows/ci.yml`:

```yaml
name: ci
on:
  push:
    branches: [main]
  pull_request:
permissions:
  contents: read
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-build -c Release --logger "trx;LogFileName=results.trx"
      - name: Benchmark against the committed baseline (output-bytes gate; CPU and wall are informational off-machine)
        run: dotnet run -c Release --no-build --project src/Plinth.Bench -- --iterations 5
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results
          path: tests/Plinth.Tests/TestResults/results.trx
  image:
    runs-on: ubuntu-latest
    needs: test
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-buildx-action@v3
      - uses: docker/build-push-action@v6
        with:
          context: .
          push: false
          tags: plinth:ci
```

`.github/workflows/release.yml`:

```yaml
name: release
on:
  push:
    tags: ['v*']
permissions:
  contents: read
  packages: write
jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-qemu-action@v3
      - uses: docker/setup-buildx-action@v3
      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - id: meta
        uses: docker/metadata-action@v5
        with:
          images: ghcr.io/ollie789/plinth
          tags: |
            type=semver,pattern={{version}}
            type=raw,value=latest
      - uses: docker/build-push-action@v6
        with:
          context: .
          push: true
          platforms: linux/amd64,linux/arm64
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
```

If Task 8 found no arm64 native package, set `platforms: linux/amd64` only.

- [ ] **Step 2: Commit, push the branch, open the PR, watch CI once**

```bash
git add -A && git commit -m "CI: test and benchmark on every push; publish the image on tags"
git push -u origin HEAD
gh pr create --base main --title "Plinth runtime: API, CLI, stores, Docker and CI" --body-file - <<'MSG'
## Summary

The two front doors on Plinth.Core and everything they need: the fetch policy with allowlist, redirect re-check and private-address block; output stores (filesystem, Azure Blob, memory, null); request signing; a recipe catalog; the shared pipeline; the HTTP API (`/v1/image`, `/v1/inspect`, `/v1/normalize`, `/healthz`, `/version`); the CLI (`run`, `inspect`, `key`, `version`, `api`) with a two-stage batch pipeline that skips by key without fetching; one Docker image for both; CI on every push and image publishing on tags.

Spec sections 3, 5.1, 5.6, 6, 7, 8, 10, 12.3, 12.5 of `docs/2026-09-02-plinth-design.md`; plan `docs/plans/2026-09-02-plinth-runtime.md`.
MSG
gh pr checks --watch
```

Expected: the `test` job green on ubuntu (linux-x64 natives, the golden tolerances from the core plan). If a golden fails on Linux beyond the ±2 px / dhash 3 tolerances, do NOT regenerate blindly: report the exact diffs in the task report; the controller decides whether to widen the tolerance or key goldens by platform. If the bench's output-bytes gate fails on Linux, report the numbers likewise.

---

### Task 10: README and spec touch-ups

**Files:**
- Modify: `README.md`, `docs/2026-09-02-plinth-design.md`

- [ ] **Step 1: README**

Replace the README body with sections: **What it does** (two sentences), **Run the API** (Docker one-liner with `PLINTH_ALLOWED_HOSTS`, `PLINTH_STORE`, optional signing; the five routes with the `X-Plinth-*` headers), **Batch with the CLI** (`plinth run` examples for a directory, a URL list and `--refresh`; manifest line shape; exit codes), **Environment variables** (a table of every `PLINTH_*` variable, its form and default, including the two Azure ones), **Stores** (the three URI forms and the on-disk layout), **Build from source** (`dotnet test`, `dotnet run --project src/Plinth.Cli -- version`), **Benchmark** (keep the existing text), **Design** (link to the spec and both plans). No attribution lines of any kind.

- [ ] **Step 2: Spec**

In `docs/2026-09-02-plinth-design.md`: section 6.2, replace the `--refresh` sentence with "`--refresh` refetches every source whose key exists and reprocesses only when the sha256 of the fetched bytes differs from the stored record's; conditional requests (ETag / If-Modified-Since) are a later optimisation"; section 7, list the three store URI forms and the `PLINTH_AZURE_STORAGE_*` variables; section 8, note `plinth` is the image entrypoint and `api` the subcommand; section 12.3, note the CLI runs libvips at concurrency 1 per process worker (`Engine.Init(1)`), and that "memory limits" means the pixel cap.

- [ ] **Step 3: Test, commit, push**

```bash
dotnet test && git add -A && git commit -m "README for the API, CLI, stores and Docker; spec touch-ups" && git push
```

---

## Self-review against the spec

- **3 Architecture**: Core pure (Task 1 only adds cancellation and a factory); Pipeline owns `ISourceFetcher`/`IOutputStore` (Tasks 2–5); Api and Cli thin (6, 7); one image (8); CI (9). The spec placed stores "in Core" loosely; this plan puts them in `Plinth.Pipeline` to keep Core I/O-free — Task 10 records that.
- **5.1 Fetch**: Task 4 — allowlist, empty refuses all, https only, 3 redirects re-checked, private-address block at connect time, 12 MB by header and stream, 12 s, fixed UA.
- **5.6 Failure posture**: API redirect/502 (6); CLI manifest continues (7); pipeline never throws for a bad source (5).
- **6.1 API**: routes, headers, env, store check-before-process and write-after (6, 5).
- **6.2 CLI**: all five commands, input forms, skip by key without fetching, `--refresh` by sha, manifest, exit codes (7).
- **7 Stores**: interface, layout, fs/azblob/memory/null (2, 3).
- **8 Deployment**: Docker with aspnet base, entrypoint `plinth`, `api` subcommand (8); GHCR on `v*` tags (9). Azure Container Apps provisioning is an operations step for the owner, not code.
- **10 Security**: SSRF (4), signing (5, 6), body caps (4, 6), pinned packages (constraints).
- **12.3/12.5**: two-stage CLI pipeline, `Engine.Init(1)` in the CLI, server GC in the container, warm-replica decision left to the owner.
- **Not here**: section 9 (Mahir swap) → its own plan in the Mahir repo; conditional refetch headers → deferred (10).

Placeholder scan: none. Type consistency: `FetchResult(Bytes, FinalUrl, ContentType)`, `PipelineResult(Status, Bytes, Record, FromStore)`, `StoredOutput(Bytes, Record)`, `RecipeCatalog.Get/Names/DefaultOnly/FromJson`, `PipelineOptions(Fetch, StoreUri, Recipes, SigningKey, OnFailure, Concurrency)`, `ApiHost.Build(args, options, configure)`, `CliApp.Build(fetcherFactory, stdout)`, `RunCommand.ResolveRecipe` — used identically across Tasks 4–7.
