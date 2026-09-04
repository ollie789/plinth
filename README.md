# Plinth

## What it does

Plinth takes retailer pack shots photographed at wildly different scales and
framings, trims each to its content, and re-centres it on one canvas so a
grid of products from many sellers reads as one catalogue. It ships as a
single container with two front doors — an HTTP API for on-demand use and a
CLI for batch runs at ingest — over one deterministic core: same bytes and
recipe always produce the same output, keyed by content, so reruns skip what
already exists.

## In production

Plinth serves every product image on [LASTLOOK](https://live.lastlook.com.au),
a multi-seller sale site over the Click Frenzy feed. The numbers below are
from the full catalogue run of 4 September 2026, engine 1.5, not a sample.

| | |
|---|---|
| Catalogue | 58,416 primary images across 36 collections; 98% on the provider's own CDN, the rest from seven retailer hosts, in jpeg, png, webp and gif |
| Normalised | 44,800 carded (77%); 12,848 passed through as editorial or already framed |
| Failed | 72 (0.12%), every one a source that returned 403, 404 or 410, or exceeded the size cap |
| Engine time | 113 ms per image mean, 89 ms median; 1.8 core-hours for the whole run |
| Wall clock | 31 minutes at 48 workers on a 14-core laptop — network-bound on the source CDN, not CPU-bound |
| Tile size | 47 KB mean, 36 KB median; 2.2 GB for all 44,800 |
| Serving | one 0.5 vCPU / 1 GiB container, about 40 ms on a store miss, Blob reads otherwise |

Scaled linearly, a million images is roughly 30 core-hours of processing and
36 GB of tiles. Run at ingest, where the bytes are already in hand, the
network cost disappears and the same catalogue processes in about eight
minutes on the same laptop.

## Run the API

```
docker run --rm -p 8080:8080 \
  -e PLINTH_ALLOWED_HOSTS=images.example.com,cdn.example.com \
  -e PLINTH_STORE=none \
  ghcr.io/ollie789/plinth:latest api
```

To run the working tree instead of the published image, build it first and
swap the image name for `plinth:dev`:

```
docker build -t plinth:dev .
```

The container runs as uid **1654** (`$APP_UID` on the .NET base image), so an
`fs://` store on a mounted volume has to be writable by that uid — otherwise
every put fails and nothing is ever cached. Either `chown 1654` the host
directory or run with `--user` set to an id that owns it.

`PLINTH_ALLOWED_HOSTS` is the security model: it's a comma list of exact
hostnames, `https` only, and an empty (or unset) list refuses every fetch —
there is no "allow all". `PLINTH_STORE` is `none` (pass-through, the
default), `fs://<path>`, or `azblob://<container>`.

Some feeds carry a thumbnail URL when the host will serve the master for the
same request, so a small per-host table rewrites the URL before it is fetched
or keyed: an `m.media-amazon.com` size token (`._AC_SY445_.`) is dropped to
reach the master, and an `assets.adidas.com` `w_500` path becomes `w_1200`.
The rewrite keeps the host, so it never widens the allowlist, and an unknown
host is left alone. Only the last Amazon token and the first adidas width
segment are touched, and a URL list is upgraded before it is deduped, so two
spellings of one source are fetched and reported once.

| Route | Purpose |
|---|---|
| `GET /v1/image?src=<url>[&recipe=<name>][&sig=<hmac>]` | The normalised image. `400` if `src` is missing or not on the allowlist, `403` on a bad signature; a failed normalise redirects to `src` (302, default) or returns `502` with the result record as JSON, per `PLINTH_ON_FAILURE`. |
| `HEAD /v1/image?…` | The same work and the same headers as `GET`, with no body — for a CDN or a prober asking whether a key is servable. |
| `GET /v1/inspect?src=<url>[&recipe=<name>][&sig=<hmac>]` | The result record as JSON, no image — for tuning and debugging. Validated and rate-gated exactly like `/v1/image`, and it counts against `PLINTH_MAX_INFLIGHT` like any other pipeline call; on a store hit it reads the record alone and never moves the image bytes. |
| `POST /v1/normalize?[recipe=<name>]` | Raw image bytes as the body, image out. `400` on an empty body, `413` once the body exceeds `PLINTH_MAX_BYTES` (20 MB by default), `403` if signing is on and `X-Plinth-Signature` does not verify, `422` with the record as JSON if normalising fails. |
| `GET /healthz` | Liveness, plus the process counters: `{"status":"ok","hits":n,"misses":n,"failed":n}` — store hits, results processed, and results that failed, since start. They are per process: behind a scaler each replica counts its own, so consecutive reads can go down as well as up. |
| `GET /version` | Engine version, libvips version, active recipe names. |

Successful and failed image responses alike carry `X-Plinth-Key`,
`X-Plinth-Status`, `X-Plinth-Cache: hit|miss` (whether the result came from
the configured store), `X-Plinth-Verdict` and `X-Plinth-Confidence`. A served
image also gets `Content-Type`, `ETag` (the output key in quotes — the key
already identifies the exact bytes) and
`Cache-Control: public, max-age=31536000, immutable`. Everything else — the
302, the 502, the 422, and every 400 and 403 — carries
`Cache-Control: no-store`, so a failure is never cached anywhere.

`PLINTH_MAX_INFLIGHT` (default 4) bounds how many requests are inside the
pipeline at once. Beyond it requests **queue**, they are not rejected: a
burst costs latency, and peak memory stays a function of that number rather
than of how many connections happen to be open.

Each pipeline call writes one JSON log line on stdout under the category
`Plinth.Api`, with `Route`, `Host` (the source host only — never the URL,
never the signature), `Key`, `Status`, `Cache` and `Ms`.

### Signing

Set `PLINTH_SIGNING_KEY=<secret>` and every `src` route requires a valid
`sig`. This is what stops a public endpoint being used as a free image
normaliser, and it is effectively mandatory for any deployment reachable from
the internet: without it, anyone can vary the query string forever, and each
variant is a fresh cache miss that costs a fetch and a full normalise. The
allowlist alone does not close that — the cache-busting happens before the
host is ever looked at.

- `sig` is the lowercase hex **HMAC-SHA256** of the exact decoded `src`
  string, with the key taken as UTF-8 bytes. Sign the URL as you mean it,
  then percent-encode it for the query string: `encodeURIComponent(src)`.
- `POST /v1/normalize` has no `src` to sign, so it takes the header
  `X-Plinth-Signature`: the same HMAC, over the **lowercase hex sha256 of the
  request body**.
- Signed URLs do not expire. Rotating `PLINTH_SIGNING_KEY` is the only way to
  revoke one, and it revokes all of them at once.

```js
const sig = hmacSha256Hex(key, src);                       // over the raw src
const url = `/v1/image?src=${encodeURIComponent(src)}&sig=${sig}`;
```

## Recipes

A recipe is the small set of choices that define an output. It serialises to
canonical JSON (sorted keys, no whitespace) and hashes into every output key,
so changing any field gives every image a new key rather than a stale tile.
`--recipe` and `?recipe=` take a name from `PLINTH_RECIPES` or a path to a
recipe JSON file; a partial object inherits the rest from these defaults.

| Field | Default | Meaning |
|---|---|---|
| `aspect` | `4:5` | Output canvas ratio |
| `width` | `1000` | Canvas width in px; height follows the aspect |
| `contentShare` | `0.85` | The trimmed product is fitted inside a box this share of the canvas on both axes (850×1062 on the default canvas) |
| `upscale` | `true` | Allow enlarging a small source to reach the content box |
| `background` | `#ffffff` | Canvas ground, and the flatten colour for transparency |
| `trimThreshold` | `12` | Max distance from the sampled ground for a pixel to count as ground |
| `format` | `webp` | `webp` or `png` |
| `quality` | `84` | Encoder quality where the format has one |
| `editorial` | `passthrough` | What to do with an image the verdict says is not a pack shot |

A pack shot on an off-white studio ground is balanced before it is carded:
every channel is scaled so the sampled ground lands exactly on the recipe
background, which removes the tinted rectangle the trimmed box would otherwise
carry onto the card. Shadows survive, and the product brightens by the same
5–8% the backdrop does. It applies to grounds between 2 and 40 levels from the
background, and the record says so in `ground.balanced`.

Wide content gets more of the tile: a trimmed box at least 1.6 times wider
than it is tall is fitted to 92% of the tile width rather than `contentShare`,
with the height box unchanged. Footwear is why — a trimmed shoe runs 2.0–2.3
wide to tall, and at 85% of the width it covers about a third of a 4:5 tile.
The two numbers are fixed in the engine, not recipe fields.

The verdict decides what happens: a model on a studio backdrop, a room or a
rug in a lounge is not a pack shot, and shrinking one onto a white card makes
a grey box floating on white — so under the default `editorial` policy it
passes through untouched (`status: "passthrough"`, `passthroughReason:
"editorial"`) and only `editorial: "card"` trims and cards it anyway. The same
policy covers a pack shot that needs nothing done to it: content that already
fills 90% of its frame **on both axes**, in a frame no wider than the canvas,
comes back untouched with `passthroughReason: "framed"`, because carding it
would only shrink the product behind a margin. Both conditions matter. A
frypan lying across a square frame fills 95% of the width and a fifth of the
height, and a shoe shot 2.2 times wider than tall fills its frame completely;
handed back as-is, each one meets a 4:5 tile that crops the handle off the
first and shows the middle third of the second. Those are carded. A pack shot photographed on its own grey ground cards on that
grey, edge to edge.

Editorial passthrough returns the retailer's original bytes and format, which
may be larger than a tile; formats a browser cannot show (tiff, heif) are
carded instead.

## Batch with the CLI

```
plinth run --input <file|dir|-> --output <dir|fs://path|azblob://container|none>
           [--recipe <name|file>] [--concurrency 8] [--manifest out.jsonl] [--refresh]
plinth inspect <file|url> [--recipe <name|file>]     # normalise and print the record
plinth key <file|url> [--recipe <name|file>]         # print the output key, nothing else
plinth version                                       # engine and libvips versions
plinth api                                           # host the HTTP API
```

```
plinth run --input tests/fixtures/src --output /tmp/out --manifest /tmp/out/manifest.jsonl
plinth run --input urls.txt --output azblob://tiles --concurrency 8
plinth run --input urls.txt --output /tmp/out --refresh
```

`--input` is a directory of images, a file of URLs (one per line, `#`
comments allowed, `-` for stdin), or a single path. `--output` is a directory
(or a bare/`fs://` path), `azblob://<container>`, or `none`. Every item is
keyed from its URL or content hash alone and **skipped without fetching** if
that key already exists in the store.

`--refresh` is the only way to invalidate anything: outputs are content-keyed
and served `immutable` for a year, so a CDN will not come back and ask again.
It refetches URL sources whose key exists and reprocesses only when the
sha256 of the freshly fetched bytes differs from the stored record's (status
`unchanged` otherwise); local file paths under `--refresh` are always
reprocessed, since there's nothing to refetch. A source that changed at the
same URL gets a new key, so the old tile is not overwritten — it is simply no
longer referenced.

The manifest is one JSON line per item, written to `--manifest <path>` or
stdout, and **every input item produces exactly one line**. An item that
reached the pixels — `ok`, `passthrough` or `failed` — is written as its full
result record, so the manifest doubles as the verdict and timing report:

```json
{"key":"63f50d…","sourceId":"https://img.clickfrenzy.com.au/deals/…-thumbnail.jpg","engineVersion":"1.5","libvipsVersion":"8.18.6","recipeHash":"dea02044224e2f31","status":"ok","error":null,"passthroughReason":null,"source":{"sha256":"1b6cc7…","bytes":197428,"width":2048,"height":2048,"format":"jpeg","hadAlpha":false,"orientationApplied":1},"ground":{"sampled":"#f5f5f5","cornerSpread":0,"cornersAgree":true,"matchesBackground":true,"balanced":true},"trim":{"left":220,"top":1164,"width":1544,"height":716,"noop":false,"contentShareBefore":0.7539},"verdict":{"packShot":true,"confidence":1,"reasons":[]},"output":{"width":1000,"height":1250,"bytes":64838,"format":"webp"},"timingsMs":{"inspect":0,"measure":26,"decode":0,"render":62,"encode":0,"total":89}}
```

An item that was never processed — `skipped` (the key was already in the
store) or `unchanged` (`--refresh` found identical bytes) — has no record to
write, so it gets the three fields that identify it:

```json
{"key":"73829b…","sourceId":"sha256:03f167…","status":"skipped"}
```

Exit code is `0` on a normal run — an unreadable file, a dead host or a store
that rejects one put is that item's `failed` line, not the run's exit code —
`2` if the run itself broke (input path not found, a store URI that would not
open, a recipe that would not resolve), and `1` on a command-line usage error.

## Environment variables

| Variable | Form | Default |
|---|---|---|
| `PLINTH_ALLOWED_HOSTS` | comma list of hostnames | empty (refuses every fetch) |
| `PLINTH_STORE` | `none` \| `fs://<path>` \| `azblob://<container>` | `none` |
| `PLINTH_SIGNING_KEY` | secret string | unset (signing off) |
| `PLINTH_ON_FAILURE` | `redirect` \| `error` | `redirect` |
| `PLINTH_MAX_INFLIGHT` | positive integer | `4` (requests beyond it queue on the API's gate) |
| `PLINTH_MAX_BYTES` | positive integer | `20971520` (20 MB) — the largest source the fetcher will pull |
| `PLINTH_RECIPES` | path to a JSON map of named recipes | unset (only the built-in `default` recipe) |
| `PLINTH_CONCURRENCY` | positive integer | processor count (the API and `plinth inspect`; `plinth run` always runs libvips at 1 per `--concurrency` process worker) |
| `PLINTH_AZURE_STORAGE_CONNECTION` | Azure Storage connection string | unset |
| `PLINTH_AZURE_STORAGE_ACCOUNT` | `https://<account>.blob.core.windows.net` | unset |

A relative `PLINTH_RECIPES` path resolves against the working directory,
which is `/app` inside the container — mount the file there, or give an
absolute path.

`PLINTH_AZURE_STORAGE_CONNECTION` and `PLINTH_AZURE_STORAGE_ACCOUNT` are only
read when the store is `azblob://…`: the connection string wins if both are
set, otherwise the account URL authenticates via `DefaultAzureCredential`
(managed identity). One of the two must be set or the store fails to open.

## Stores

`PLINTH_STORE` (API) and `--output` (CLI) both take one of three forms:

- `none` — nothing is remembered; every request is processed fresh.
- `fs://<path>` (or a bare path with the CLI) — a directory on disk.
- `azblob://<container>` — one Azure Blob container.

Every store implementation uses the same on-disk/blob layout, sharded by the
first two hex characters of the key:

```
<root>/<key[0..2]>/<key>.<ext>   the normalised image (ext from the output format, e.g. webp)
<root>/<key[0..2]>/<key>.json    the result record
```

The filesystem store writes each file under a unique `.tmp` name and renames
it into place, so a reader never sees a half-written file. A crash mid-write
can leave a `<key>.<ext>.<hex>.tmp` behind; nothing ever reads those, and
they are safe to delete at any time.

## Build from source

Requires the .NET 10 SDK.

```
dotnet test
dotnet run --project src/Plinth.Cli -- version
```

## Benchmark

`dotnet run -c Release --project src/Plinth.Bench` normalises every fixture
twenty times (`--iterations N` to change it; CI uses `--iterations 5`) and
compares against `docs/bench/baseline.json`. It runs with `editorial: "card"`
so every fixture goes through the render path, including the ones the verdict
calls editorial — a passthrough measures nothing and would report the source
size as an output size.

Only **max output bytes** gates: it is deterministic, so a change there means
the encoder or the pipeline changed, and the run exits non-zero once it is
more than 20% over the baseline. Mean CPU per image and max wall p95 are
reported but never fail a build on their own, because they move with whatever
else the machine is doing — pass `--strict` to gate on those too.
`--write-baseline` resets the baseline after an intentional change.

## Operating it

If you are deploying Plinth, rolling the engine, or warming a catalogue, read
`docs/RUNBOOK.md` first. Two of the things in it — merge before you tag, and
bump the consumer's engine constant in the same change — fail silently rather
than loudly, and both have cost a debugging session already.

## Design

The full design lives in `docs/2026-09-02-plinth-design.md`; the build plans
that produced this code are `docs/plans/2026-09-02-plinth-core.md` and
`docs/plans/2026-09-02-plinth-runtime.md`.
