# Running Plinth

Everything an operator needs that the code cannot tell you. The design spec
(`docs/2026-09-02-plinth-design.md`) says what Plinth is and why; this says how
to change it without breaking the site that depends on it.

## Where it runs

| | |
|---|---|
| Image | `ghcr.io/ollie789/plinth:<tag>`, published by `.github/workflows/release.yml` on a `v*` tag |
| Host | Azure Container App `plinth` in resource group `rg-plinth`, environment `plinth-env` |
| Store | Azure Blob container `tiles`; the account name is in `deploy/azure/.state/storage` |
| Consumer | LASTLOOK (`ollie789/mahir`), `lib/image-engine.ts` |

A tile's identity is `sha256(sourceId | recipeHash | engineVersion)`. Blobs are
sharded one level: `<key[:2]>/<key>.json` for the record and `<key[:2]>/<key>.<ext>`
for the image. **The engine version is part of the key**, so rolling the engine
invalidates the entire store by design.

Requests are signed `HMAC-SHA256` over the exact `src` string, key as UTF-8,
lowercase hex. The signature covers **the src alone** — other query parameters
ride outside it, which is what makes the cache-busting parameter below possible.

## The two rules that will bite you

Both of these cost real time on 4 Sep 2026. Neither announces itself: the
symptom in both cases is code that looks deployed and isn't.

### 1. Merge before you tag

`release.yml` builds from whatever the tag points at. Tagging a branch you have
not merged yet cuts the release from the *old* commit and publishes it under the
new version number. Nothing fails. You get an image that is correctly named and
contains none of your work.

The only reason this was caught is that the deploy ran before the build had
finished publishing, so it errored with `MANIFEST_UNKNOWN` instead of quietly
succeeding.

### 2. Bump `PLINTH_ENGINE` in the same change

Plinth serves every tile `public, max-age=31536000, immutable`. The signed URL
is therefore the cache key for every layer between Plinth and the shopper: the
image optimiser, the CDN, the browser.

A new engine returns **different bytes for the same src** — that is what an
engine roll *is*. If the URL does not change with it, nothing downstream has any
reason to ask again, and a year's `max-age` means effectively never.

So `lib/image-engine.ts` in the consumer carries `PLINTH_ENGINE` and appends
`&v=<version>` to every URL. Plinth ignores the parameter. It exists purely so
the URL moves when the tiles do. **Bump it in the same change that rolls the
Container App**, or production keeps serving the old tiles and looks perfectly
healthy while doing it.

## Rolling a new engine

```bash
# 1. Merge first. Always.
gh pr merge <n> --merge --delete-branch --repo ollie789/plinth

# 2. Tag the merged main.
git checkout main && git pull && git tag v0.7.0 && git push origin v0.7.0

# 3. WAIT for the release build (~5 min). Confirm the image exists before deploying:
gh run watch --repo ollie789/plinth

# 4. Deploy.
az containerapp update -n plinth -g rg-plinth --image ghcr.io/ollie789/plinth:0.7.0

# 5. Confirm what is actually running.
curl -s https://<fqdn>/version
```

**Never run `deploy/azure/provision.sh` to deploy.** It generates a new signing
key when `PLINTH_SIGNING_KEY` is unset, which would invalidate every URL the
consumer is serving. It is for provisioning, not rollout.

Then bump `PLINTH_ENGINE` in the consumer and ship that, or see rule 2.

## Warming the catalogue

After an engine roll the store is cold. Traffic warms it on its own at roughly
40 ms a miss, but a full warm gets ahead of it.

Enumerate the catalogue from the provider feed (`FEED_BASE_URL` / `FEED_TOKEN`,
bearer auth): `/v1/collections?status=all`, then
`/v1/collections/<slug>/products?page=N&per_page=250`, taking `images[0].src`
for every product. Only the primary image is mapped through Plinth.

```bash
export PLINTH_AZURE_STORAGE_CONNECTION="$(az storage account show-connection-string \
  -n "$(cat deploy/azure/.state/storage)" -g rg-plinth -o tsv)"
export PLINTH_ALLOWED_HOSTS="<same list as the Container App>"
dotnet run -c Release --project src/Plinth.Cli -- \
  run -i catalogue.txt -o azblob://tiles --concurrency 48 --manifest warm.jsonl
```

This processes locally and writes to the same store the API reads, so it does
not load the Container App at all. Reference point: 58,416 images in 31 minutes
at 45 img/s on a 14-core laptop.

A plain re-run **skips anything already keyed without fetching it**, so it is
cheap but only picks up new products. To notice a source that has broken since
it was warmed you need `--refresh`, which re-fetches everything and reprocesses
only what actually changed.

## When an image fails

`PLINTH_ON_FAILURE=redirect` (the default) sends a 302 to the original source.
That splits failures into two groups that deserve opposite handling.

**Oversized** — over `MaxBytes` (12 MB). The redirect is followed and the
original is served, so the product appears, just without the canvas. Leave these
alone. Verified: a 16.7 MB JPEG reached the page as a 54 KB webp.

**Dead at source** — 403, 404, 410. The redirect points at nothing, the
optimiser returns 502, and the tile renders as an empty box with a price under
it. These are not Plinth's failures and no fallback can help; a browser with a
normal user-agent and referer gets the same 403.

The consumer drops them at `feedToInternal`, from the list in
`lib/unfetchable-images.ts`, regenerated by `tools/unfetchable-images.mjs` from a
run manifest. Refresh it after each full warm. It must contain whole source
URLs — a bare host there would empty an entire sale floor, and there is a test
that says so.

Dropping happens in the mapper rather than the browser because the page's claims
("N products we found from $X") are derived from what is on the floor. Hiding a
tile client-side would leave the claim counting products nobody can see.

## Verifying a roll

1. `curl /version` reports the engine you deployed.
2. `curl /healthz` shows `failed: 0`. Counters are **per-replica** and the app
   scales to 3, so consecutive reads can go down as well as up. Do not use them
   to test a single request.
3. The consumer's pages emit `&v=<new engine>` on every tile URL.
4. A tile that should be carded comes back at the canvas aspect. Fetching the
   page's own `/_next/image` URL and checking the pixel dimensions is the only
   check that covers the whole chain; everything upstream can look right while
   the shopper still gets a stale image.

## Secrets

The signing key lives in `deploy/azure/.state/signing-key`, git-ignored, and is
mirrored into the consumer's `PLINTH_SIGNING_KEY` (Production, Sensitive).
Never print it, never commit it, never pass it on a command line that lands in
shell history. Rotating it invalidates every URL in flight.

## Known open, as of 4 Sep 2026

- **MaxBytes is 12 MB.** 21 catalogue images exceed it. Raising it to 20 MB
  would recover them; the decision is unmade.
- **The consumer's tiles are 3:4, the canvas is 4:5.** Every tile loses about
  6% of its width to the crop. Wide items sit at 92% of the canvas, so they
  clear it by under a percent.
- **The quick-view gallery bypasses the engine.** Only `images[0]` is mapped;
  the gallery's other images are served raw, so it visibly flips from a
  normalised card to the seller's own framing.
- **The unfetchable list is a snapshot.** A source that dies between warms shows
  an empty tile until the next one. Making it self-healing needs somewhere to
  persist live verification.
- **Dependabot** major bumps to `release.yml` are not covered by CI, which only
  runs `ci.yml`. Prove them with a prerelease tag — `:latest` only moves for
  non-prereleases — before trusting the next real release.
