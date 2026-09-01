# Plinth — design

*2 September 2026. Status: draft for owner review.*

Plinth puts every product on the same plinth. It takes retailer pack shots
that were photographed at wildly different scales and framings, trims each to
its content, and re-centres it on one canvas so a grid of products from many
sellers reads as one catalogue. It ships as a single open-source container
that LASTLOOK and Click Frenzy (CF) can each run inside their own pipelines.

## 1. Why this exists

LASTLOOK's multi-seller feed mixes pack shots from fifteen retailer CDNs.
The Iconic ships a shoe at 20% of its frame, Amazon at 98%, Myer at 83%.
No amount of tile styling hides that. The image engine on `main` in the
Mahir repo (`lib/image-engine.ts`, `app/img-engine/route.ts`) already
solves it for us as an on-demand Next.js route built on sharp, and it works:
every product whole, centred, same scale, one ground.

CF has the same problem across every seller they scrape and declined to
build it because of compute cost at their scale. That objection comes from
picturing a naive "process everything on every scrape" model. Plinth is
designed so that model never happens: each unique image is processed once,
keyed by content, and never again.

The owner's goal (2 Sep): a shared open-source tool that both sides run
themselves. **No change to the CF feed contract.** CF running it internally
is the win; anything in the feed is out of scope.

## 2. Goals and non-goals

Goals:

- One deterministic core that turns a pack shot into a normalised tile plus
  a result record, identical output for identical input.
- Two thin front doors over that core: an HTTP API for on-demand use and a
  CLI for batch runs at ingest.
- Incremental by construction: outputs are content-addressed, reruns skip
  what exists.
- Safe to expose: allowlisted sources, size and pixel caps, no proxying into
  private networks.
- Replace LASTLOOK's Next.js engine route with the same container, so we
  dogfood exactly what CF would run.

Non-goals for v1:

- No classifier making trim decisions. The pack-shot verdict is **reported**
  on every image; the host allowlist still **decides**.
- No AVIF or per-width resizing. Vercel's image optimiser does that in front
  of us; CF has its own CDN.
- No multi-tenant auth, accounts or quotas.
- No feed changes, no new fields for CF to emit.
- No background removal, recolouring or generative work. The contract leaves
  room for them (see 4.4) but nothing is built.

## 3. Architecture

![System overview](diagrams/system-overview.svg)

One repository, MIT licensed, `ollie789/plinth`, public from the first
commit. .NET 10 LTS. Imaging via NetVips, the .NET binding for libvips,
which is the same engine sharp uses, so quality and speed match the current
route. NetVips is MIT; libvips is LGPL and dynamically linked.

Layout:

```
plinth/
  src/Plinth.Core/     pure library: recipe, pipeline, verdict, key, stores
  src/Plinth.Api/      ASP.NET minimal API wrapping Core
  src/Plinth.Cli/      System.CommandLine tool wrapping Core
  tests/Plinth.Tests/  xUnit: golden fixtures, determinism, policy, CLI
  docs/                this spec, diagrams, API and CLI reference
  Dockerfile           one image, entrypoint selects api or cli
  .github/workflows/   build, test, publish image to GHCR on tags
```

The Core has no knowledge of HTTP, files or Azure. Everything that touches
the outside world sits behind two interfaces:

- `ISourceFetcher` — bytes for a URL, under the fetch policy (5.1).
- `IOutputStore` — put and get by key. Implementations: filesystem,
  Azure Blob, in-memory. The API's default is pass-through (no store).

Why a container and not a library: Vercel cannot run .NET in-process, and CF's
stack is unknown. A container with an HTTP interface is the one shape both
can run without caring what is inside.

## 4. The core contract

This is the foundation. Everything else can be rewritten behind it.

### 4.1 Recipe

The small set of choices that define an output. Defaults are today's engine.

| Field | Default | Meaning |
|---|---|---|
| `aspect` | `4:5` | Output canvas ratio |
| `width` | `1000` | Canvas width in px; height follows aspect |
| `contentShare` | `0.78` | The trimmed product is fitted inside a box that is this share of the canvas on both axes |
| `upscale` | `true` | Allow enlarging small sources to reach the content box (today's behaviour) |
| `background` | `#ffffff` | Canvas ground, also the flatten colour for transparency |
| `trimThreshold` | `12` | Max distance from the sampled ground for a pixel to count as ground |
| `format` | `webp` | `webp` or `png` |
| `quality` | `84` | Encoder quality where the format has one |

A recipe serialises to canonical JSON (sorted keys, no whitespace) and hashes
to `recipeHash` (first 16 hex of SHA-256).

### 4.2 Key

```
sourceId = canonical URL when the image was fetched
         = "sha256:" + hex(sha256(bytes)) when bytes were supplied directly
key      = sha256( sourceId + "|" + recipeHash + "|" + engineVersion )
```

The key is addressable **from the URL alone**, without fetching. That is
what makes batch reruns free (skip by key, no download) and what lets a
consumer compute an output's location with no lookup table. The record
carries the content hash of the bytes actually processed, so a silent change
behind the same URL is detectable with `--refresh`, and identical images at
different URLs can be deduplicated later by a content-hash index.

`engineVersion` is the **algorithm** version, bumped only when output would
change, not on every package release. Bumping it invalidates every cached
output on purpose; that is the cost of never serving a stale tile.

The key is the filename in every store and the cache identity in every URL.

### 4.3 Normalise

```
NormalizeResult Normalize(ReadOnlySpan<byte> source, Recipe recipe)
```

Pure and deterministic: same bytes and recipe produce byte-identical output
and an identical record. No network, no clock, no randomness.

### 4.4 Result record

Emitted for every image, success or failure. Stored beside the output as
`<key>.json`, returned as headers by the API, written to the CLI manifest.

```json
{
  "key": "…",
  "engineVersion": "1.0.0",
  "recipeHash": "…",
  "source": { "sha256": "…", "bytes": 184233, "width": 1600, "height": 2000, "format": "jpeg", "hadAlpha": false, "orientationApplied": 1 },
  "ground": { "sampled": "#fefefe", "cornerSpread": 2, "cornersAgree": true },
  "trim": { "left": 310, "top": 120, "width": 980, "height": 1690, "contentShareBefore": 0.61 },
  "verdict": { "packShot": true, "confidence": 0.93, "reasons": [] },
  "output": { "width": 1000, "height": 1250, "bytes": 61240, "format": "webp" },
  "timingsMs": { "decode": 18, "measure": 6, "trim": 4, "canvas": 11, "encode": 22 },
  "status": "ok",
  "error": null
}
```

Future operations (colour extraction, dedupe by perceptual hash, background
removal) extend this record; they do not change the function signature.

## 5. The pipeline

![Per-image pipeline](diagrams/per-image-pipeline.svg)

### 5.1 Fetch

Only the front doors fetch; Core takes bytes.

- The host must be on the allowlist. An empty allowlist refuses everything;
  there is no "allow all" switch. Hosts are exact hostname matches over
  `https` only.
- Redirects: at most three, and **every hop is re-checked** against the
  allowlist and against a private-address block (loopback, RFC 1918,
  link-local, unique-local, metadata endpoints). A redirect to anywhere else
  fails the fetch.
- Caps: 12 MB body (checked on `Content-Length` and again on the read
  stream), 12 s timeout, a fixed User-Agent that names the tool.

### 5.2 Normalise

- Apply EXIF orientation, then strip all metadata.
- Convert to sRGB using the embedded ICC profile if present.
- Refuse images over 30 megapixels before decoding (decompression bombs).
- Flatten alpha onto the recipe background so there is one ground to measure.
- Animated formats take the first frame.

### 5.3 Measure and trim

- Sample the four corners (8×8 patches). The ground is the median colour;
  `cornerSpread` is the max distance between corners.
- Measure on a working copy no larger than 512 px on the long side, then
  scale the box back to source coordinates and crop the full-resolution
  image. Roughly ten times cheaper than trimming at full size, same box.
- Trim with libvips `find_trim` against the sampled ground at
  `trimThreshold`. Threshold 12 rides JPEG noise on studio backgrounds
  without eating light products; it is a recipe field so it can be tuned.
- If the box is empty or covers the whole frame, the trim is a no-op and the
  verdict says why.

### 5.4 Verdict

A heuristic score in `[0, 1]` with reasons, **reported only** in v1:

- Corners agree (spread under threshold) and are near the recipe background.
- The trimmed box touches at most one frame edge.
- Content share before trim is between 0.05 and 0.98.
- The box aspect is not extreme (no thin strips).

Each failed test subtracts and adds a reason string. This is the data we
tune on real seller traffic before anything depends on it.

### 5.5 Canvas and encode

- Scale the trimmed content to fit the content box (`contentShare` of the
  canvas on both axes), enlarging if `upscale` allows.
- Composite centred on the recipe background at the recipe size.
- Encode per recipe. Output is unchanged by the caller's viewport; resizing
  is the CDN's job.

### 5.6 Failure posture

Any failure in any step yields a record with `status: "failed"` and an
`error`. The API then **redirects to the original source** (302) by default,
so a tile with an untrimmed image beats a tile with no image. The CLI records
the failure in the manifest and continues.

## 6. Front doors

### 6.1 HTTP API

| Route | Purpose |
|---|---|
| `GET /v1/image?src=<url>[&recipe=<name>][&sig=<hmac>]` | Normalised image. Headers: `Content-Type`, `Cache-Control: public, max-age=31536000, immutable`, `X-Plinth-Key`, `X-Plinth-Verdict`, `X-Plinth-Confidence`, `X-Plinth-Status`. |
| `GET /v1/inspect?src=<url>` | The result record as JSON, no image. For tuning and debugging. |
| `POST /v1/normalize` | Bytes in (multipart or raw body), image out, record in headers. For callers that already hold the bytes. |
| `GET /healthz` | Liveness. |
| `GET /version` | Engine version, libvips version, active recipe names. |

Configuration by environment: `PLINTH_ALLOWED_HOSTS` (comma list),
`PLINTH_SIGNING_KEY` (optional; when set, `sig` must be a valid HMAC of
`src`), `PLINTH_STORE` (`none` | `fs://path` | `azblob://container`),
`PLINTH_ON_FAILURE` (`redirect` | `error`), `PLINTH_RECIPES` (path to a
JSON map of named recipes; `default` is always present).

When a store is configured the API checks it by key before processing and
writes to it after, so on-demand and batch share one cache.

### 6.2 CLI

```
plinth run --input <file|dir|->  --output <dir|azblob://container>
           [--recipe <name|file>] [--concurrency 4] [--manifest out.jsonl]
plinth inspect <file|url>
plinth key <file> [--recipe …]
plinth version
```

`--input` accepts a directory of images, a file of URLs (one per line, `-`
for stdin), or a single path. Every item is fetched under the same policy as
the API, hashed, keyed, and **skipped if the key already exists in the
output store**. Concurrency defaults to the core count. The manifest is one
JSON record per line, failures included. Exit code is non-zero only if the
run itself broke, not if individual images failed; the manifest carries
those.

This is the mode CF would run inside their scrape, and the reason the
compute objection does not apply: reruns cost only what is new.

## 7. Storage

`IOutputStore` with `TryGet(key)`, `Put(key, bytes, record)`,
`Exists(key)`. Implementations in v1:

- `FileSystemStore` — `<root>/<key[0..2]>/<key>.<ext>` plus `<key>.json`.
- `AzureBlobStore` — same layout inside one container, public read, immutable
  cache headers set on upload. Uses the Azure SDK for .NET with a connection
  string or managed identity.
- `MemoryStore` — tests.
- `NullStore` — API pass-through when no store is configured.

Storage is a layer, not a dependency: CF can add S3 or GCS by implementing
one interface.

## 8. Deployment

- **Docker**: `mcr.microsoft.com/dotnet/aspnet:10.0` runtime image with
  `NetVips.Native.linux-x64`. The CLI project references the API project;
  the entrypoint is `plinth`, and `plinth api` starts the server in-process.
  One image, one tag, two modes.
- **CI**: GitHub Actions on every push runs build and tests; on a `v*` tag it
  publishes `ghcr.io/ollie789/plinth:<version>` and `:latest`. Engine version
  comes from the tag and is stamped into the assembly.
- **LASTLOOK hosting**: Azure Container Apps, 0.5 vCPU / 1 GiB, ingress
  public, `minReplicas: 1` in production so the first tile of the day does
  not wait for a cold start (a scale-to-zero container plus libvips takes
  several seconds to wake and Vercel's image optimiser gives up on slow
  upstreams). Store is Azure Blob in the same region behind Azure CDN or a
  custom domain such as `img.lastlook.com.au`.
- **CF hosting**: their call. The image runs anywhere Docker does.

## 9. LASTLOOK integration (Mahir repo, separate branch, after v1 ships)

The container, backed by the Blob store, replaces the Next.js route:

1. The mapper's `engineImageUrl` emits the Plinth API URL
   (`https://img.lastlook.com.au/v1/image?src=…&sig=…`) instead of
   `/img-engine`, still wrapped by `/_next/image` for per-width resizing and
   AVIF. The Plinth host is added to `next.config.ts` image
   `remotePatterns`. No per-product state: the key derives from the URL.
2. The API serves from the Blob store when the key exists and processes,
   stores and serves when it does not, so the first request for any image is
   never a broken tile and every later one is a Blob read.
3. Feed sync pre-warms: after each sync it requests every engine-host image
   once, so shoppers almost never pay the processing latency. This is a
   fire-and-forget step; a failure here changes nothing for the page.
4. `lib/image-engine.ts` keeps `ENGINE_HOSTS` as the source of truth and the
   same list is the container's `PLINTH_ALLOWED_HOSTS`. The `/img-engine`
   route stays as a dormant fallback for one release, then retires.

Nothing in the feed contract changes.

## 10. Security

- SSRF: allowlist plus per-hop redirect checks plus private-address block
  (5.1). Nothing a URL parameter invents is ever fetched.
- Abuse: an optional HMAC on `src` stops the public endpoint being used as
  a free image normaliser; Container Apps ingress rate limits sit in front.
- Resource: byte cap, pixel cap, timeout, bounded concurrency in the CLI,
  libvips' own memory limits configured at startup.
- Supply chain: NetVips and its native package are pinned; the image is
  built from the official Microsoft base; Dependabot on.

## 11. Testing

- **Golden fixtures**: one real sample from each of the nine hosts the
  current engine was verified against, checked in at reduced size, with the
  expected record and a perceptual hash of the expected output. A run must
  match the record exactly and the output within a small hash distance.
- **Determinism**: same bytes and recipe twice produce identical keys and
  byte-identical output.
- **Policy**: private-address refusal, redirect re-check, empty allowlist
  refuses, byte and pixel caps, animated first frame, EXIF rotation, alpha
  flatten.
- **Verdict**: a small labelled set of pack shots and editorial shots; the
  score must separate them at 0.5 with margin.
- **CLI**: a second run over the same input performs zero fetches and zero
  normalisations; `--refresh` refetches and reprocesses only where the
  content hash changed.
- **API**: contract tests for every route, including the redirect on
  failure and the store round-trip.

## 12. Observability

Structured JSON logs with the key, host, status and step timings. `/healthz`
for the platform. Timings live in the record, so a batch manifest doubles as
the performance report. Metrics export is a later addition.

## 13. Decisions taken

- .NET, because the owner maintains it. Containers make the language
  invisible to CF.
- NetVips over ImageSharp: same engine as today, faster, no licence key
  needed for commercial use.
- Ingest-first over on-demand-only: it is CF's only viable mode, it makes
  the compute argument true, and it removes cold-start risk from our tiles.
- Verdict reported, not enforced, until tuned on real traffic.
- No feed changes.

## 14. Open items

- White-on-white products are where trim fails (a shaved sneaker toe). The
  verdict's confidence and `cornerSpread` exist to catch it; the labelled
  sample for tuning is the first job once the core runs.
- Azure subscription, resource group and the custom domain for the store.
- Whether LASTLOOK's warm replica is worth its small monthly cost versus
  relying entirely on pre-warming at sync.
- Recipe naming for CF: whether they want the LASTLOOK default or their own.
