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
