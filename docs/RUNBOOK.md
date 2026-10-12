# Running Plinth

Everything an operator needs that the code cannot tell you. The design spec
(`docs/2026-09-02-plinth-design.md`) says what Plinth is and why; this says how
to change it without breaking the site that depends on it.

Since 12 October 2026 production runs on Vercel. The Azure deployment it
replaced is retired; what is left of it is at the end of this file.

## Where it runs

| | |
|---|---|
| Host | Vercel container function: team `lastlook`, project `plinth`, region `syd1` (set in `vercel.json`) |
| Public URL | `https://plinth-rho.vercel.app`, the production domain and the only public address. Plinth is the owner's own technology and deliberately stays off LASTLOOK's domain |
| Build | `Dockerfile.vercel`, which `vercel.json` declares as the project's one container service; images land in Vercel Container Registry (`vercel vcr image ls plinth`) |
| Deploys | Merging to `main` deploys production in about a minute (the container build is about 35 s). Any other branch gets a preview deployment |
| Settings | Non-secret ones are `ENV` lines in `Dockerfile.vercel`; `PLINTH_SIGNING_KEY` is a Sensitive variable on the Vercel project (Production and Preview) |
| Store | None (`PLINTH_STORE=none`). Vercel's CDN caches each tile; the consumer's image optimiser caches each resized copy |
| Consumer | LASTLOOK (`ollie789/mahir`): `lib/image-engine.ts` and `lib/image-hosts.ts`, plus the `PLINTH_BASE_URL` and `PLINTH_SIGNING_KEY` variables |
| Published image | `ghcr.io/ollie789/plinth:<tag>`, built from `Dockerfile` by `.github/workflows/release.yml` on a `v*` tag. It is for anyone hosting Plinth elsewhere; LASTLOOK production does not use it |

Every address except the production domain sits behind Vercel Authentication:
`plinth-lastlook.vercel.app`, the per-deployment URLs, and all previews. Reach
those with `vercel curl <url> --scope lastlook`; a plain `curl` gets a 302 to a
login page.

A tile's identity is `sha256(sourceId | recipeHash | engineVersion)`. Requests
are signed `HMAC-SHA256` over the exact `src` string, key as UTF-8, lowercase
hex. The signature covers **the src alone**. Other query parameters ride
outside it, which is what makes the cache-busting parameter below possible.

## How a tile reaches a shopper

1. The consumer's feed mapper turns each product image into a signed URL,
   `https://plinth-rho.vercel.app/v1/image?src=…&sig=…&v=<PLINTH_ENGINE>`.
2. That URL is baked into the consumer's stored page copies. Its warm job
   rebuilds them stalest first, and every copy turns over in about ten
   minutes.
3. The shopper's browser asks the consumer's `/_next/image` for a width. The
   optimiser fetches the Plinth URL, with Vercel's CDN in front of Plinth.
4. On a CDN miss, Plinth fetches the source, normalises it, and returns a
   webp marked `public, max-age=31536000, immutable`.

The consequence to remember: **any change to the URL takes about ten minutes
to reach every page.** That covers a new engine, a new host, a new key and the
off switch. Until a page's copy is rebuilt it keeps the old URL.

## The rules that will bite you

Most of these have already cost real time, and none of them announces itself.

### 1. Merging to `main` is a production deploy

There is no tag step and no deploy command any more. A merge goes live in
about a minute, so check CI before merging, not after. A build that fails on
Vercel never replaces the running deployment, so a broken `Dockerfile.vercel`
costs a failed deploy, not an outage. A change that builds but behaves badly
goes live, and the fix is a rollback (see below).

### 2. Bump `PLINTH_ENGINE` in the consumer when the bytes change

Plinth serves every tile `immutable` for a year, so the signed URL is the cache
key for every layer between Plinth and the shopper: Vercel's CDN, the image
optimiser and the browser. A new engine returns **different bytes for the same
src**. That is what an engine roll *is*. If the URL does not change with it,
nothing downstream has any reason to ask again.

So `lib/image-engine.ts` in the consumer carries `PLINTH_ENGINE` and appends
`&v=<version>` to every URL. Plinth ignores the parameter. It exists purely so
the URL moves when the tiles do. Ship the consumer change **after** the Plinth
deploy is live, or production keeps serving the old tiles and looks perfectly
healthy while doing it.

### 3. A missing signing key opens Plinth to anyone

Plinth only checks signatures when `PLINTH_SIGNING_KEY` is set. Without it,
every request is accepted, and a public URL becomes a free image normaliser on
our bill. The key must be set for **both** Production and Preview on the
Vercel project. After any change to the project's variables, confirm an
unsigned request still gets a 403 (see Verifying).

### 4. `vercel.json` is load-bearing

Without its `services` entry, Vercel's build skips `Dockerfile.vercel`
entirely, deploys nothing, and every route answers 404. That happened on the
first deploy (12 Oct 2026, build CLI 62.7). Without `regions: ["syd1"]` the
function runs in Washington (`iad1`), and every uncached tile crosses the
Pacific to fetch an Australian source.

### 5. Two Dockerfiles, two host lists

`Dockerfile` builds the published ghcr image; `Dockerfile.vercel` builds
production. Their build stages are copies, so change both. The allowed hosts
live in `Dockerfile.vercel` (`PLINTH_ALLOWED_HOSTS`) and must match the
consumer's `PLINTH_HOSTS` in `lib/image-engine.ts`. See Changing the allowed
hosts for the order.

### 6. After a rollback, merges stop going live

An Instant Rollback turns off Vercel's automatic promotion. Later merges to
`main` still build, but they do not replace the rolled-back deployment until
you promote one (see Rolling back).

## Deploying a change

1. Open a PR. CI (`ci.yml`) builds and runs the tests; wait for it to pass.
2. Optionally push the branch first. Its preview deployment runs the real
   container, so you can check it with
   `vercel curl https://<preview-url>/healthz --scope lastlook`.
3. Merge. Vercel builds and promotes it in about a minute. Watch it with
   `vercel ls --scope lastlook` from a checkout linked once with
   `vercel link --project plinth --scope lastlook`, or on the project's
   Deployments page.
4. Run the checks under Verifying.

## Rolling a new engine

1. Merge the PR that bumps `Engine.Version`. It is live about a minute later.
2. Confirm `curl -s https://plinth-rho.vercel.app/version` reports the new
   engine.
3. Bump `PLINTH_ENGINE` in the consumer's `lib/image-engine.ts` and push.
4. Pages pick up the new `&v=` as the consumer's warm job rebuilds them,
   which takes about ten minutes. Then check a page's tiles (Verifying, step 5).

There is no warming step, because there is no store. The first request for a
tile costs a source fetch, about 90–110 ms of processing (the 4 Sep catalogue
run measured 89 ms median, 113 ms mean), and sometimes a cold start (below).
Every repeat comes from the CDN.

## Rolling back

**Plinth itself.** Use Instant Rollback, either from the project's Production
tile in the dashboard or with
`vercel rollback <deployment-url> --scope lastlook`. It takes effect at once.
Rollback restores the old build *with its old variables*, so changing a
variable afterwards does nothing to it. Merges stop going live until you
promote a deployment: `vercel promote <deployment-url> --scope lastlook`, or
**Undo Rollback** on the Production tile.

**The consumer's off switch.** Use this when Plinth is down, slow or wrong and
a rollback will not fix it. Delete the `plinth-rho.vercel.app` line from
`IMAGE_HOSTS` in the consumer's `lib/image-hosts.ts` and push. The consumer
only signs Plinth URLs when Plinth's host is on that list. Without it,
provider images go straight to the image optimiser with the seller's own
framing, and the handful of retailer hosts fall back to the consumer's older
built-in `/img-engine` route. Putting the line back turns Plinth on again.

The switch takes about ten minutes to reach every page (see How a tile
reaches a shopper). Until a page is rebuilt, its tiles still name Plinth, and
the consumer hands those URLs to the browser directly. If Plinth is hanging,
those pages stay slow until they turn over. That is what happened on
12 Oct 2026: the Azure host stopped answering, every tile hung for about 37 s,
and each edit page's load event fired about 38 s in, long after most
shoppers had acted.

## Changing the allowed hosts

Plinth answers an unlisted host with `400 source not allowed`, which renders
as an empty tile. So:

- **Adding a host:** add it to `PLINTH_ALLOWED_HOSTS` in `Dockerfile.vercel`
  and merge first; then add it to the consumer's `PLINTH_HOSTS`.
- **Removing a host:** remove it from the consumer first, wait about ten
  minutes for pages to turn over, then remove it from Plinth.

Hosts are exact names, `https` only, and an empty list refuses everything.

## When an image fails

`PLINTH_ON_FAILURE=redirect` sends a 302 to the original source. That splits
failures into two groups that deserve opposite handling.

**Oversized:** over `PLINTH_MAX_BYTES` (20 MB) or `PLINTH_MAX_PIXELS` (20 MP).
The redirect is followed and the original is served, so the product appears,
just without the canvas. Leave these alone. Verified: a 16.7 MB JPEG reached
the page as a 54 KB webp.

**Dead at source:** 403, 404 or 410. The redirect points at nothing, the
optimiser returns 502, and the tile renders as an empty box with a price under
it. These are not Plinth's failures and no fallback can help. A browser with a
normal user agent and referer gets the same 403.

The consumer drops them at `feedToInternal`, using the list in
`lib/unfetchable-images.ts`. That list is regenerated by
`tools/unfetchable-images.mjs` from a catalogue run's manifest. To make one,
list the catalogue with the consumer's `tools/catalogue-images.ts` (it walks
the provider feed once, and the provider is sensitive to request volume, so
run it rarely and never in a loop). Then process the list locally without a
store:

```bash
PLINTH_ALLOWED_HOSTS="<the list in Dockerfile.vercel>" \
  dotnet run -c Release --project src/Plinth.Cli -- \
  run -i catalogue.txt -o none --concurrency 48 --manifest run.jsonl
```

The list must contain whole source URLs. A bare host there would empty an
entire sale floor, and a test in the consumer says so. Dropping happens in the
mapper rather than the browser because each page's claims ("N products we
found from $X") are derived from what is on the floor. Hiding a tile
client-side would leave the claim counting products nobody can see.

## Verifying

1. **Health and region:** `curl -sD - https://plinth-rho.vercel.app/healthz`
   should return 200 and `"status":"ok"`, with a header like
   `x-vercel-id: syd1::syd1::…`. The second segment is the function's region;
   `iad1` there means `regions` was lost. With no store, `store` is always
   `"ok"`. The counters are per instance, and instances come and go, so never
   read them as totals.
2. **Engine:** `curl -s https://plinth-rho.vercel.app/version` reports the
   engine and libvips versions.
3. **Signing is on:** an unsigned request must be refused.
   ```bash
   curl -s -o /dev/null -w "%{http_code}\n" \
     "https://plinth-rho.vercel.app/v1/image?src=https%3A%2F%2Fimg.clickfrenzy.com.au%2Fx.jpg"
   ```
   It must print `403`. Anything else, usually a `302` to the missing source,
   means the signature check is off because `PLINTH_SIGNING_KEY` is missing
   (rule 3).
4. **A real tile:** take a signed URL from a live page, so the key never
   leaves the consumer.
   ```bash
   TILE=$(curl -s https://live.lastlook.com.au/edits/books-edit \
     | grep -oE 'https%3A%2F%2Fplinth-rho\.vercel\.app[^&"]+' | head -1 \
     | python3 -c 'import sys,urllib.parse;print(urllib.parse.unquote(sys.stdin.read().strip()))')
   curl -s -o /dev/null -w "%{http_code} %{content_type} %{size_download}B\n" "$TILE"
   ```
   It should print `200 image/webp`. An empty `TILE` means the page carries
   no Plinth URLs (the off switch is in, or the copies have not turned over
   yet).
5. **The whole chain:** the page's tile URLs carry the expected `&v=`, and a
   tile that should be carded comes back at the canvas aspect. Fetching the
   page's own `/_next/image` URL and checking the pixel dimensions is the only
   check that covers the whole chain. Everything upstream can look right while
   the shopper still gets a stale image.

## Monitoring

The consumer's warm job (`app/api/admin/warm-pages`, every two minutes) asks
`/healthz` on the Plinth it is serving (`lib/plinth-health.ts`). Two failures
in a row send an email to `OPS_ALERT_EMAIL`, a Production variable on the
consumer, sent through its Resend domain. While Plinth stays down the email
repeats at most every six hours, and one more goes out when it recovers. The
watch state lives in the consumer's Runtime Cache, so it carries across runs.
Every failure also logs `[plinth] down: …` in the consumer's logs, and the
cron's JSON reports `plinth: { ok, status, ms, fails, sent }`.

With the off switch in, the consumer serves no Plinth, so it checks nothing
and cannot alert. Turning the switch back on resumes the checks.

## Limits on Vercel

- **Memory:** each instance is Vercel's Standard size, 2 GB and 1 vCPU (Pro
  teams can change it under Settings → Functions). Peak use is roughly
  `MaxInFlight × (MaxPixels × 4 bytes + MaxBytes) + ~150 MB runtime`. At
  production's `PLINTH_MAX_INFLIGHT=4` and `PLINTH_MAX_PIXELS=20000000` that
  is about 550 MB. Every image the catalogue has shipped is under 18.4 MP.
  Anything over the cap is refused at the header and, under redirect, served
  uncarded from the source.
- **In-flight work:** each instance processes at most four images at a time
  (`PLINTH_MAX_INFLIGHT`); any more queue on its gate.
- **Cold starts:** an instance shuts down after five minutes without traffic.
  The first request after that waits for a start, about 1.2 s measured in
  `syd1`. Azure ran a permanently warm replica because the image optimiser
  gives up on slow upstreams. One to two seconds has been fine in testing,
  and the consumer's watchdog asks every two minutes, which keeps an instance
  warm (see Monitoring).
- **Duration:** 300 s per request, Vercel's default, far beyond any tile.
- **Logs:** the dashboard's Logs tab for the project, or
  `vercel logs --project plinth --scope lastlook`. A container's stdout goes
  to every request in flight on that instance, so match lines by the request
  log's `Key` rather than by timing.
- **Registry:** each deploy stores an image of about 104 MB in Vercel
  Container Registry, at $0.10 per GB-month. Prune old ones now and then with
  `vercel vcr image ls plinth --scope lastlook` and
  `vercel vcr image rm plinth <image-id> --scope lastlook`.

## Running it locally

- Tests: `dotnet test`.
- The API from source:
  `ASPNETCORE_HTTP_PORTS=8080 PLINTH_ALLOWED_HOSTS=img.clickfrenzy.com.au dotnet run --project src/Plinth.Cli -- api`.
  No `PLINTH_SIGNING_KEY` means signing is off, which is fine locally and
  never anywhere public.
- The production container:
  `docker build -f Dockerfile.vercel -t plinth:vercel . && docker run --rm -p 8080:80 -e PLINTH_SIGNING_KEY=dev plinth:vercel`.
  `vercel dev` runs the same image through Vercel's local runtime, and needs
  Docker running.

## Secrets

`PLINTH_SIGNING_KEY` is set in three places, and all three must match:

- the `plinth` project's Production variables;
- the `plinth` project's Preview variables;
- the consumer's Production `PLINTH_SIGNING_KEY`.

All three are Sensitive, so Vercel never shows the value again. The one
readable copy belongs in the team password manager; the key rotated on
12 Oct 2026 was generated into `~/.config/lastlook/plinth-signing-key`
(mode 600) on the operator's machine for that purpose. Never print the key, commit it,
or put it on a command line that lands in shell history. Pipe it from a file
instead:
`tr -d '\n' < key | vercel env add PLINTH_SIGNING_KEY production --sensitive --scope lastlook`.

**Rotating it.** Last rotated on 12 Oct 2026, after the key had spent a month
on a personal Azure account. Plinth holds one key at a time, and the consumer's stored
pages carry signatures made with the old one. Swapping the key in place would
break every tile until the pages turn over. To rotate without broken tiles:

1. Turn the consumer's off switch (Rolling back) and wait about ten minutes,
   until a live page carries no Plinth URLs (Verifying, step 4 returns an
   empty `TILE`).
2. Generate a key (`openssl rand -hex 32`) and set it in all three places
   above, replacing the old value.
3. Redeploy both. A new variable value only reaches a new deployment:
   `vercel redeploy <plinth-production-url> --target production --scope lastlook`,
   and a push or redeploy for the consumer.
4. Put the host line back in the consumer and push. Every tile is processed
   once more, because the signature is part of its URL.

## Azure (retired 12 Oct 2026)

From 2 Sep to 12 Oct 2026 Plinth ran as Azure Container App `plinth` in
resource group `rg-plinth` (Australia East), with tiles warmed into Blob
container `tiles` on storage account `plinth4bd6bf1c`. The subscription was
cancelled on 12 Oct, and the storage account now answers `AccountIsDisabled`.

Still to delete, by the subscription owner:

- resource group `rg-plinth`, which holds the Container App, its environment
  `plinth-env` and the storage account;
- alerts `plinth-not-running` and `plinth-restarting`, and action group
  `plinth-alerts`, which emails a personal address.

Azure typically deletes a cancelled subscription's data on its own about 90
days after cancellation. The provisioning script `deploy/azure/provision.sh` was removed
when Plinth moved; recover it with `git show dcc095f:deploy/azure/provision.sh`.
The `azblob://` store is still supported for anyone hosting Plinth on Azure.

## Known open, as of 12 Oct 2026

- **The consumer's tiles are 3:4, the canvas is 4:5.** Every tile loses about
  6% of its width to the crop. Wide items sit at 92% of the canvas, so they
  clear it by under a percent.
- **The unfetchable list is a snapshot.** A source that dies between catalogue
  runs shows an empty tile until the next one. Making it self-healing needs
  somewhere to persist live verification.
- **Dependabot** major bumps to `release.yml` are not covered by CI, which only
  runs `ci.yml`. Prove them with a prerelease tag (`:latest` only moves for
  non-prereleases) before trusting the next published image. They do not
  affect production, which builds from `Dockerfile.vercel`.
