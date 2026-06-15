// Cloudflare Worker entry. Two jobs:
//   1. /hf/*  → reverse-proxy to huggingface.co so transformers.js downloads
//      models from OUR origin. The app is cross-origin isolated (COEP via
//      coi-serviceworker for SharedArrayBuffer / multi-threaded WASM), which
//      blocks direct cross-origin fetches to huggingface.co with a CORS error.
//      Proxying makes those requests same-origin, sidestepping CORS/COEP.
//   2. everything else → static assets. The assets binding is configured with
//      not_found_handling: "single-page-application", so unknown routes return
//      index.html (Blazor client-side routing / deep-link refresh).
export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname.startsWith("/hf/")) {
      // Strip the "/hf/" prefix; the rest maps 1:1 onto huggingface.co.
      const target = "https://huggingface.co/" + url.pathname.slice(4) + url.search;

      // Forward only method + Range (HF resolve URLs 302 to a CDN; following the
      // redirect server-side means the browser never sees a cross-origin hop).
      const init = { method: request.method, headers: {}, redirect: "follow" };
      const range = request.headers.get("Range");
      if (range) init.headers["Range"] = range;

      const upstream = await fetch(target, init);

      // Re-emit with permissive resource headers so the response is usable under
      // cross-origin isolation. Stream the body (no buffering) for large weights.
      const headers = new Headers(upstream.headers);
      headers.set("Access-Control-Allow-Origin", "*");
      headers.set("Cross-Origin-Resource-Policy", "cross-origin");
      headers.delete("Content-Security-Policy");
      return new Response(upstream.body, {
        status: upstream.status,
        statusText: upstream.statusText,
        headers,
      });
    }

    // /lib/ contains large, infrequently-changing runtime bundles (e.g. the
    // Kuzu/LadybugDB WASM bundle at ~8 MB). Give them a long-lived cache so the
    // browser can reuse the compiled WASM across sessions. Content doesn't change
    // unless the package version is bumped, so stale risk is low.
    if (url.pathname.startsWith("/lib/")) {
      const res = await env.ASSETS.fetch(request);
      const headers = new Headers(res.headers);
      headers.set("Cache-Control", "public, max-age=86400, stale-while-revalidate=604800");
      return new Response(res.body, { status: res.status, statusText: res.statusText, headers });
    }

    const res = await env.ASSETS.fetch(request);

    // Our hand-written JS modules/workers under /js/ are NOT fingerprinted, so a
    // browser-cached copy silently shadows a redeploy (you keep running old code
    // even after `wrangler deploy`). Force no-store so every load refetches them.
    if (url.pathname.startsWith("/js/")) {
      const headers = new Headers(res.headers);
      headers.set("Cache-Control", "no-store");
      return new Response(res.body, { status: res.status, statusText: res.statusText, headers });
    }
    return res;
  },
};
