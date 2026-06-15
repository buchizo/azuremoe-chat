// Cache API wrapper for the GraphDB binary.
// Keyed by the manifest's databaseSha256 so DB updates always re-download
// (even if the filename stays the same), and stale versions are evicted.
const CACHE = 'azuremoe-db-v1';
const key = (sha256) => `/~db/${sha256}`;

export async function isDbCached(sha256) {
  const c = await caches.open(CACHE);
  return !!(await c.match(key(sha256)));
}

export async function fetchDbCached(sha256) {
  const c = await caches.open(CACHE);
  const r = await c.match(key(sha256));
  if (!r) throw new Error('DB not in cache: ' + sha256);
  return new Uint8Array(await r.arrayBuffer());
}

// bytes is a Uint8Array from Blazor interop. Evicts all old versions first.
export async function storeDbInCache(sha256, bytes) {
  const c = await caches.open(CACHE);
  const old = await c.keys();
  await Promise.all(old.map(k => c.delete(k)));
  await c.put(key(sha256), new Response(bytes, {
    headers: {
      'Content-Type': 'application/octet-stream',
      'Content-Length': String(bytes.length),
    },
  }));
}
