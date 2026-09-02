# Plinth

## What it does

Plinth takes retailer pack shots photographed at wildly different scales and
framings, trims each to its content, and re-centres it on one canvas so a
grid of products from many sellers reads as one catalogue. It ships as a
single container with two front doors — an HTTP API for on-demand use and a
CLI for batch runs at ingest — over one deterministic core: same bytes and
recipe always produce the same output, keyed by content, so reruns skip what
already exists.

## Run the API

```
docker run --rm -p 8080:8080 \
  -e PLINTH_ALLOWED_HOSTS=images.example.com,cdn.example.com \
  -e PLINTH_STORE=none \
  plinth:dev api
```

`PLINTH_ALLOWED_HOSTS` is the security model: it's a comma list of exact
hostnames, `https` only, and an empty (or unset) list refuses every fetch —
there is no "allow all". `PLINTH_STORE` is `none` (pass-through, the
default), `fs://<path>`, or `azblob://<container>`. Add
`-e PLINTH_SIGNING_KEY=<secret>` to require every request's `sig` to be a
valid HMAC of `src`, which stops the public endpoint being used as a free
image normaliser.

| Route | Purpose |
|---|---|
| `GET /v1/image?src=<url>[&recipe=<name>][&sig=<hmac>]` | Normalised image. `400` if `src` is missing or not on the allowlist, `403` on a bad signature; a failed normalise redirects to `src` (302, default) or returns `502` with the result record as JSON, per `PLINTH_ON_FAILURE`. |
| `GET /v1/inspect?src=<url>[&recipe=<name>][&sig=<hmac>]` | The result record as JSON, no image — for tuning and debugging. |
| `POST /v1/normalize?[recipe=<name>]` | Raw image bytes as the body, image out. `400` on an empty body, `413` once the body exceeds the 12 MB cap, `422` with the record as JSON if normalising fails. |
| `GET /healthz` | Liveness. |
| `GET /version` | Engine version, libvips version, active recipe names. |

Successful and failed image responses alike carry `X-Plinth-Key`,
`X-Plinth-Status`, `X-Plinth-Cache: hit|miss` (whether the result came from
the configured store), `X-Plinth-Verdict` and `X-Plinth-Confidence`; a
served image also gets `Content-Type` and
`Cache-Control: public, max-age=31536000, immutable`.

## Batch with the CLI

```
plinth run --input tests/fixtures/src --output /tmp/out --manifest /tmp/out/manifest.jsonl
plinth run --input urls.txt --output azblob://tiles --concurrency 8
plinth run --input urls.txt --output /tmp/out --refresh
```

`--input` is a directory of images, a file of URLs (one per line, `-` for
stdin), or a single path. `--output` is a directory (or a bare/`fs://` path),
`azblob://<container>`, or `none`. Every item is keyed from its URL or
content hash alone and **skipped without fetching** if that key already
exists in the store. `--refresh` refetches URL sources whose key exists and
reprocesses only when the sha256 of the freshly fetched bytes differs from
the stored record's (status `unchanged` otherwise); local file paths under
`--refresh` are always reprocessed, since there's nothing to refetch.

The manifest is one JSON line per item, written to `--manifest <path>` or
stdout:

```json
{"key":"ba301d87ff8bf5244ee6e618b3527772711410331556e78aa412e606cb8945ec","sourceId":"sha256:5e696310fd9e8d44ed94b37af3eefb77987efea300f3860ee3866804d163fb40","status":"ok","error":null,"outputBytes":55898}
```

`status` is `ok`, `passthrough`, `failed`, `skipped` or `unchanged`. Exit
code is `0` on a normal run (individual item failures live in the manifest,
not the exit code), `2` if the run itself broke (bad input path, a store
that failed to open), `1` on a command-line usage error.

## Environment variables

| Variable | Form | Default |
|---|---|---|
| `PLINTH_ALLOWED_HOSTS` | comma list of hostnames | empty (refuses every fetch) |
| `PLINTH_STORE` | `none` \| `fs://<path>` \| `azblob://<container>` | `none` |
| `PLINTH_SIGNING_KEY` | secret string | unset (signing off) |
| `PLINTH_ON_FAILURE` | `redirect` \| `error` | `redirect` |
| `PLINTH_RECIPES` | path to a JSON map of named recipes | unset (only the built-in `default` recipe) |
| `PLINTH_MAX_INFLIGHT` | positive integer | `4` (requests beyond it queue on the API's gate) |
| `PLINTH_CONCURRENCY` | positive integer | processor count (the API and `plinth inspect`; `plinth run` always runs libvips at 1 per `--concurrency` process worker) |
| `PLINTH_AZURE_STORAGE_CONNECTION` | Azure Storage connection string | unset |
| `PLINTH_AZURE_STORAGE_ACCOUNT` | `https://<account>.blob.core.windows.net` | unset |

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

## Build from source

Requires the .NET 10 SDK.

```
dotnet test
dotnet run --project src/Plinth.Cli -- version
```

## Benchmark

`dotnet run -c Release --project src/Plinth.Bench` normalises every fixture
twenty times and compares against `docs/bench/baseline.json`; it exits
non-zero on a regression over 20%. `--write-baseline` resets the baseline
after an intentional change.

## Design

Design: `docs/2026-09-02-plinth-design.md`. Build plans:
`docs/plans/2026-09-02-plinth-core.md`, `docs/plans/2026-09-02-plinth-runtime.md`.
