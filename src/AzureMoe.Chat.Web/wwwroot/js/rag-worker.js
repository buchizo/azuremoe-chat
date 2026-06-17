// RAG pipeline Web Worker: combines LadybugDB graph queries + transformers.js
// embedding in one worker so the entire retrieval phase (embed → gather → rank)
// runs without any Blazor↔JS bridge crossings during a query turn.
// Communicates with the main thread via rag-interop.js using a typed message
// protocol: { id, type, payload } / { id, type: "done"|"progress"|"error", payload }.
import lbug from "../lib/ladybug/index.js";
import { pipeline, env } from "https://esm.sh/@huggingface/transformers@4";

// HF proxy: production is cross-origin isolated (COEP credentialless via
// coi-serviceworker). Route model fetches through the same-origin /hf proxy so
// they bypass CORS/COEP. Localhost dev talks to HF directly.
const isLocalDev = self.location.hostname === "localhost" || self.location.hostname === "127.0.0.1";
if (!isLocalDev) {
  env.remoteHost = self.location.origin;
  env.remotePathTemplate = "hf/{model}/resolve/{revision}/";
}
// Single-threaded WASM avoids the SharedArrayBuffer multi-thread path that
// produced degenerate vectors in earlier builds. WebGPU is attempted first.
env.backends.onnx.wasm.numThreads = 1;

// ── State ──────────────────────────────────────────────────────────────────
let conn = null;
let db   = null;
let extractor = null;
let _embeddingDevice = "unknown";
let _debugLog = null; // string[] when debug is enabled, null otherwise

// ── Utilities ──────────────────────────────────────────────────────────────

// BigInt → Number for JSON-safe DB row objects (same as ladybug-worker.js).
const plain = (v) => JSON.parse(JSON.stringify(v, (_, val) =>
  typeof val === "bigint" ? Number(val) : val));

function esc(s) {
  return s.replace(/\\/g, "\\\\").replace(/'/g, "\\'");
}

function idList(ids) {
  return "[" + ids.join(",") + "]";
}

// ── Constants (mirrors RetrievalEngine.cs) ─────────────────────────────────
const AUTHORITY_K = 100;
const GRAPH_BONUS = 0.05;
const DATE_BOOST  = 0.05;
const SHARED_CAP  = 5;

// ── Synchronous DB query helpers ───────────────────────────────────────────

function runQuery(cypher) {
  const r = conn.query(cypher);
  if (!r.isSuccess()) {
    const msg = r.getErrorMessage();
    r.close();
    throw new Error(`cypher error: ${msg}`);
  }
  const rows = plain(r.getAllObjects());
  r.close();
  if (_debugLog !== null) _debugLog.push(`[debug] [GraphDB]\n${cypher}\n→ ${rows.length} 件`);
  return rows;
}

function runQueryWithVec(cypher, vec) {
  const ps = conn.prepare(cypher);
  if (!ps.isSuccess()) {
    const msg = ps.getErrorMessage();
    ps.close?.();
    throw new Error(`prepare error: ${msg}`);
  }
  const r = conn.execute(ps, { qv: vec });
  ps.close?.();
  if (!r.isSuccess()) {
    const msg = r.getErrorMessage();
    r.close();
    throw new Error(`cypher error: ${msg}`);
  }
  const rows = plain(r.getAllObjects());
  r.close();
  // Log query text but omit the embedding vector ($qv) since it is thousands of floats.
  if (_debugLog !== null) _debugLog.push(`[debug] [GraphDB with $qv]\n${cypher}\n→ ${rows.length} 件`);
  return rows;
}

function readChunk(row) {
  return {
    title:    row.title    ?? "",
    date:     row.date     ?? "",
    url:      row.url      ?? "",
    text:     row.text     ?? "",
    distance: typeof row.distance === "number" ? row.distance : 0.0,
    cid:      typeof row.cid      === "number" ? row.cid      : 0,
  };
}

// ── Keyword search (no embedding required) ─────────────────────────────────

// Simple keyword search against post titles and tag names.
// Used on memory-constrained devices (mobile) where the embedding model is
// skipped. Searches each keyword independently and deduplicates by chunk id.
function keywordSearch(keywords, topK) {
  if (keywords.length === 0) return [];
  const seen = new Set();
  const results = [];

  function addRows(rows) {
    for (const row of rows) {
      const c = readChunk(row);
      if (!c.cid || seen.has(c.cid)) continue;
      seen.add(c.cid);
      results.push(c);
    }
  }

  for (const kw of keywords) {
    if (results.length >= topK) break;
    const escaped = esc(kw);
    try {
      addRows(runQuery(
        `MATCH (p:Post)-[:HAS_CHUNK]->(c:Chunk)
WHERE p.title CONTAINS '${escaped}'
RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, 0.0 AS distance
ORDER BY p.date DESC, c.ordinal ASC
LIMIT ${topK}`));
    } catch (e) { console.warn("[rag-worker] keyword title search error:", e?.message); }

    if (results.length >= topK) break;
    try {
      addRows(runQuery(
        `MATCH (t:Tag)<-[:TAGGED]-(p:Post)-[:HAS_CHUNK]->(c:Chunk)
WHERE t.name CONTAINS '${escaped}'
RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, 0.0 AS distance
ORDER BY p.date DESC, c.ordinal ASC
LIMIT ${topK}`));
    } catch (e) { console.warn("[rag-worker] keyword tag search error:", e?.message); }
  }

  return results.slice(0, topK);
}

// ── Cypher query templates (ported from JsGraphStore.cs) ───────────────────

function vectorSearch(vec, topK) {
  const rows = runQueryWithVec(
    `CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_emb_idx', $qv, ${topK})
YIELD node AS c, distance
MATCH (p:Post)-[:HAS_CHUNK]->(c)
RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, distance
ORDER BY distance`,
    vec);
  return rows.map(readChunk);
}

function vectorSearchInDateRange(vec, fromIso, toIso, topK, overFetch) {
  const rows = runQueryWithVec(
    `CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_emb_idx', $qv, ${overFetch})
YIELD node AS c, distance
MATCH (p:Post)-[:HAS_CHUNK]->(c)
WHERE p.date >= '${esc(fromIso)}' AND p.date < '${esc(toIso)}'
RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, distance
ORDER BY distance
LIMIT ${topK}`,
    vec);
  return rows.map(readChunk);
}

function chunksByDateRange(fromIso, toIso, topK) {
  const rows = runQuery(
    `MATCH (p:Post)-[:HAS_CHUNK]->(c:Chunk)
WHERE p.date >= '${esc(fromIso)}' AND p.date < '${esc(toIso)}'
RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, 0.0 AS distance
ORDER BY p.date DESC, c.ordinal ASC
LIMIT ${topK}`);
  return rows.map(readChunk);
}

function expandByGraph(seedIds, limit, includeRelated) {
  if (seedIds.length === 0) return [];
  const ids = idList(seedIds);
  const hop = includeRelated ? "-[:RELATED_TO*0..1]-(e2:Entity)" : "";

  // Run 3 independent queries with per-query LIMIT so Kuzu can terminate each
  // traversal early. A single UNION ALL + ORDER BY shared + outer LIMIT forces
  // Kuzu to exhaust all branches before applying the limit (no early exit), which
  // caused 200+ second runtimes. count(DISTINCT) grouping on the text column was
  // also extremely expensive; shared is set to 1 uniformly (a tiny scoring delta).
  const seen = new Set(seedIds);  // pre-seed with vector-search results to avoid duplicates
  const results = [];

  function addRows(rows) {
    for (const row of rows) {
      const c = readChunk(row);
      if (!c.cid || seen.has(c.cid)) continue;
      seen.add(c.cid);
      results.push({ chunk: c, shared: 1 });
    }
  }

  const cap = limit * 3;

  try {
    addRows(runQuery(
      `MATCH (sp:Post)-[:HAS_CHUNK]->(seed:Chunk) WHERE seed.id IN ${ids}
MATCH (sp)-[:TAGGED]->(t:Tag)<-[:TAGGED]-(p2:Post)-[:HAS_CHUNK]->(c2:Chunk)
RETURN p2.title AS title, p2.date AS date, p2.url AS url, c2.text AS text, c2.id AS cid
LIMIT ${cap}`));
  } catch (e) { console.warn("[rag-worker] tag expand error:", e?.message); }

  try {
    addRows(runQuery(
      `MATCH (seed:Chunk)-[:MENTIONS]->(e:Entity) WHERE seed.id IN ${ids}
MATCH (e)${hop}<-[:MENTIONS]-(c2:Chunk)
MATCH (p:Post)-[:HAS_CHUNK]->(c2)
RETURN p.title AS title, p.date AS date, p.url AS url, c2.text AS text, c2.id AS cid
LIMIT ${cap}`));
  } catch (e) { console.warn("[rag-worker] entity expand error:", e?.message); }

  try {
    addRows(runQuery(
      `MATCH (sp:Post)-[:HAS_CHUNK]->(seed:Chunk) WHERE seed.id IN ${ids}
MATCH (sp)-[:COVERS_SERVICE]->(s:AzureService)<-[:COVERS_SERVICE]-(p2:Post)-[:HAS_CHUNK]->(c2:Chunk)
RETURN p2.title AS title, p2.date AS date, p2.url AS url, c2.text AS text, c2.id AS cid
LIMIT ${cap}`));
  } catch (e) { console.warn("[rag-worker] service expand error:", e?.message); }

  return results;
}

// ── Embedding ──────────────────────────────────────────────────────────────

// id is the pending message id so progress events can be correlated on the main thread.
// dotnetRef cannot be passed across postMessage (not structured-cloneable), so
// the worker posts plain progress messages and rag-interop.js calls dotnetRef there.
async function loadEmbeddingPipeline(modelId, id) {
  env.allowRemoteModels = true;
  const progressCb = (info) => {
    if (info?.status === "progress") {
      const pct = Math.round((info.loaded ?? 0) / Math.max(info.total ?? 1, 1) * 100);
      self.postMessage({ id, type: "progress", payload: { stage: "embedding", file: info.file ?? "", pct } });
    }
  };

  // WebGPU inference for the embedding model deadlocks on HTTPS under COOP/COEP
  // (D3D12 GPU fence never signals back to the JS thread). enableGraphCapture:false
  // prevents the issue at session-create time but not during readback after inference.
  // multilingual-e5-small on WASM is fast enough (~50-150 ms/call) so we skip WebGPU
  // here; WebGPU is still used for the LLM in llm-worker.js where the gain is larger.
  const p = await pipeline("feature-extraction", modelId, {
    dtype: "fp32",
    device: "wasm",
    progress_callback: progressCb,
  });
  _embeddingDevice = "wasm";
  console.log("[rag-worker] embedding device: wasm");

  // Warmup: trigger ONNX WASM kernel JIT compilation now so the first real
  // query doesn't pay the ~1.5 s compilation cost.
  try { await p("query: warmup", { pooling: "mean", normalize: true }); } catch {}

  return p;
}

async function embed(text) {
  if (!extractor) throw new Error("Embedding model not loaded");
  if (_debugLog !== null) _debugLog.push(`[debug] [Embedding] "query: ${text}"`);
  const out = await extractor("query: " + text, { pooling: "mean", normalize: true });
  const vec = Array.from(out.data);
  const badIdx = vec.findIndex(v => !Number.isFinite(v));
  if (badIdx !== -1)
    throw new Error(`embed: non-finite value at index ${badIdx} (len=${vec.length})`);
  if (vec.length === 0 || vec.length > 4096)
    throw new Error(`embed: unexpected vector length ${vec.length}`);
  return vec;
}

// ── Retrieval utilities ────────────────────────────────────────────────────

function inRange(date, dr) {
  return !!date && date >= dr.fromIso && date < dr.toIso;
}

// Lightweight keyword extraction for Deep-mode follow-up titles.
// Mirrors QueryAnalyzer.Token regex (ASCII product names + katakana runs).
const TOKEN_RE = /[A-Za-z][A-Za-z0-9.\-]{1,}|[ァ-ヶー]{2,}/g;
function extractKeywordsFromTitle(title) {
  return [...new Set((title.match(TOKEN_RE) ?? []).filter(t => t.length >= 2))].slice(0, 8);
}

// ── Phase 1: recall (gather) ───────────────────────────────────────────────

function gather(searchVec, q, opt, timer) {
  const order = [];           // insertion-ordered cid list
  const chunk = {};           // cid → ChunkResult
  const graph = {};           // cid → max shared count

  function add(c, shared) {
    if (!c.cid) return;
    if (!(c.cid in chunk)) { chunk[c.cid] = c; order.push(c.cid); }
    if ((graph[c.cid] ?? 0) < shared) graph[c.cid] = shared;
  }

  if (q.date?.hard) {
    const _s = performance.now();
    vectorSearchInDateRange(searchVec, q.date.fromIso, q.date.toIso, opt.vectorTopK, opt.dateOverFetch)
      .forEach(c => add(c, 0));
    chunksByDateRange(q.date.fromIso, q.date.toIso, opt.vectorTopK)
      .forEach(c => add(c, 0));
    if (timer) timer.vsearch += performance.now() - _s;
  } else {
    const _s = performance.now();
    vectorSearch(searchVec, opt.vectorTopK).forEach(c => add(c, 0));
    if (timer) timer.vsearch += performance.now() - _s;
  }

  if (opt.useGraph) {
    const seeds = order.slice(0, 6);
    const _s = performance.now();
    expandByGraph(seeds, opt.expansionLimit, opt.includeRelated, q.keywords ?? [])
      .forEach(({ chunk: c, shared }) => add(c, shared));
    if (timer) timer.expand += performance.now() - _s;
  }

  return order.map(cid => ({ chunk: chunk[cid], shared: graph[cid] ?? 0 }));
}

// ── Phase 2: precision rank + cutoff (port of RetrievalEngine.RankAndSelectAsync) ──

function rankAndSelect(candidates, authority, origQ, opt) {
  const hard = origQ.date?.hard === true;

  // rel: cid → cosine-similarity to original question (1 − distance)
  const rel = new Map(authority.map(a => [a.cid, 1.0 - a.distance]));

  // Pool = recall candidates ∪ authority (ensures the question's own top hits are
  // always considered even when the search query was a rewrite).
  const pool = new Map();
  candidates.forEach(c => pool.set(c.chunk.cid, c));
  authority.forEach(a => { if (!pool.has(a.cid)) pool.set(a.cid, { chunk: a, shared: 0 }); });

  let scored = [];
  for (const [, { chunk: c, shared }] of pool) {
    // Hard date filter: drop out-of-window chunks entirely.
    if (hard && !inRange(c.date, origQ.date)) continue;

    let r;
    if (rel.has(c.cid)) {
      r = rel.get(c.cid);
    } else {
      if (!hard) continue;  // non-dated: outside relevance pool → drop (precision cutoff)
      r = 0.0;              // dated: keep for coverage at lowest priority
    }

    let score = r + GRAPH_BONUS * (Math.min(shared, SHARED_CAP) / SHARED_CAP);
    if (origQ.date && !origQ.date.hard && inRange(c.date, origQ.date)) score += DATE_BOOST;
    scored.push({ chunk: c, rel: r, score });
  }

  // Never starve generation: fall back to best recall candidates if nothing ranked.
  if (scored.length === 0)
    scored = candidates.slice(0, opt.ragTopK).map(({ chunk: c }) => ({ chunk: c, rel: 0.5, score: 0.5 }));

  return scored
    .sort((a, b) => b.score - a.score)
    .slice(0, opt.ragTopK)
    .map(s => ({ ...s.chunk, distance: Math.max(0, Math.min(1, 1.0 - s.rel)) }));
}

// ── Retrieve: Fast / Normal (one recall pass) ──────────────────────────────

function retrieveOnce(searchVec, origVec, searchQ, origQ, opt, timer) {
  const candidates = gather(searchVec, searchQ, opt, timer);

  const _sa = performance.now();
  const authority = origQ.date?.hard
    ? vectorSearchInDateRange(origVec, origQ.date.fromIso, origQ.date.toIso,
        AUTHORITY_K, Math.max(opt.dateOverFetch, 600))
    : vectorSearch(origVec, AUTHORITY_K);
  if (timer) timer.auth += performance.now() - _sa;

  const _sr = performance.now();
  const result = rankAndSelect(candidates, authority, origQ, opt);
  if (timer) timer.rank += performance.now() - _sr;

  return result;
}

// ── Retrieve: Deep (multi-round gather from round-0 titles) ───────────────

async function retrieveDeep(origVec, searchVec, origQ, searchQ, opt, timer) {
  const all = new Map();  // cid → Candidate (first-wins, same as C# TryAdd)
  function merge(cs) { cs.forEach(c => { if (!all.has(c.chunk.cid)) all.set(c.chunk.cid, c); }); }

  const round0 = gather(searchVec, searchQ, opt, timer);
  merge(round0);

  // Deterministic follow-ups: embed the titles of round-0 articles.
  // Stays grounded in the corpus (no LLM query planning). The final rank against
  // the original question filters anything tangential these bring in.
  const maxFollowups = Math.max(0, (opt.deepMaxRounds ?? 3) - 1);
  const followupTitles = [...new Set(
    round0.map(c => c.chunk.title).filter(t => t && t.trim())
  )].slice(0, maxFollowups);

  for (const title of followupTitles) {
    const fv  = await embed(title);
    const tkw = extractKeywordsFromTitle(title);
    merge(gather(fv, { date: searchQ.date, keywords: tkw }, opt, timer));
  }

  const _sa = performance.now();
  const authority = origQ.date?.hard
    ? vectorSearchInDateRange(origVec, origQ.date.fromIso, origQ.date.toIso,
        AUTHORITY_K, Math.max(opt.dateOverFetch, 600))
    : vectorSearch(origVec, AUTHORITY_K);
  if (timer) timer.auth += performance.now() - _sa;

  const _sr = performance.now();
  const result = rankAndSelect([...all.values()], authority, origQ, opt);
  if (timer) timer.rank += performance.now() - _sr;

  return result;
}

// ── Message handler ────────────────────────────────────────────────────────

self.onmessage = async ({ data: { id, type, payload } }) => {
  try {
    if (type === "init") {
      self.postMessage({ id, type: "progress", payload: { stage: "db", file: "chat.db", pct: 0 } });

      try {
        await lbug.init();
        const bytes = new Uint8Array(payload.dbBytes);
        lbug.getFS().createDataFile("/", "chat.db", bytes, true, true, true);
        db   = new lbug.Database("/chat.db");
        conn = new lbug.Connection(db);
      } catch (e) {
        throw new Error(`DB init failed (dbLen=${payload?.dbBytes?.byteLength}): ${e?.message ?? e}`);
      }

      self.postMessage({ id, type: "progress", payload: { stage: "db", file: "chat.db", pct: 100 } });

      if (!payload.skipEmbedding) {
        extractor = await loadEmbeddingPipeline(payload.embeddingModelId, id);
      } else {
        _embeddingDevice = "none";
      }
      self.postMessage({ id, type: "inited", payload: { ok: true, device: _embeddingDevice } });

    } else if (type === "retrieve") {
      if (!conn) throw new Error("Not initialised — call init first");

      // No embedding model (skipEmbedding mode) — fall back to keyword search.
      if (!extractor) {
        const keywords = extractKeywordsFromTitle(payload.userQuery ?? "");
        const chunks = keywordSearch(keywords, payload.config?.ragTopK ?? 5);
        self.postMessage({ id, type: "done", payload: { chunks, debugLog: [] } });
        return;
      }

      const { userQuery, searchQuery, origQ, searchQ, mode, config: opt } = payload;
      _debugLog = opt.debug ? [] : null;

      const _t0 = performance.now();
      const searchVec = await embed(searchQuery);
      const _t1 = performance.now();
      const origVec = searchQuery !== userQuery ? await embed(userQuery) : searchVec;
      const _t2 = performance.now();

      const timer = { vsearch: 0, expand: 0, auth: 0, rank: 0 };
      const chunks = mode === "Deep"
        ? await retrieveDeep(origVec, searchVec, origQ, searchQ, opt, timer)
        : retrieveOnce(searchVec, origVec, searchQ, origQ, opt, timer);
      const _t3 = performance.now();

      console.log(
        `[rag] device:${_embeddingDevice}` +
        ` embed:${(_t2 - _t0).toFixed(0)}ms` +
        ` vsearch:${timer.vsearch.toFixed(0)}ms` +
        ` expand:${timer.expand.toFixed(0)}ms` +
        ` auth:${timer.auth.toFixed(0)}ms` +
        ` rank:${timer.rank.toFixed(0)}ms` +
        ` total:${(_t3 - _t0).toFixed(0)}ms`
      );

      const debugLog = _debugLog ?? [];
      _debugLog = null;
      self.postMessage({ id, type: "done", payload: { chunks, debugLog } });

    } else if (type === "dispose") {
      // Graceful shutdown: release ONNX WebGPU session before the worker is
      // terminated so the GPU memory is reclaimed promptly rather than
      // waiting for the browser's GC to collect the orphaned session.
      try { await extractor?.dispose?.(); } catch {}
      extractor = null;
      conn = null;
      db = null;
      self.postMessage({ id, type: "done", payload: { ok: true } });

    } else {
      throw new Error(`Unknown message type: ${type}`);
    }
  } catch (e) {
    self.postMessage({ id, type: "error", payload: { message: String(e?.message ?? e) } });
  }
};
