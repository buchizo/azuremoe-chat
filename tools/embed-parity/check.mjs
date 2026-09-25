// Parity gate for an embedding model / library upgrade. Run it before a full
// re-ingest.
//
//   1. Query token ids produced by transformers.js must equal the .NET side's
//      exactly. The query string is rebuilt here from the raw text with the same
//      normalisation rag-worker.js applies.
//   2. Query vectors must agree to cosine >= 0.999. Not bit-exact: Node's
//      onnxruntime is not the browser's WASM build.
//   Passages are reported but never fail the check: only the .NET side embeds
//   them, and transformers.js' Unigram tokenizer lacks byte_fallback, so
//   passages with newlines legitimately differ (.NET matches HF tokenizers).
//   3. (optional) A natively built .lbdb must open in the wasm engine, and its
//      vector index must answer a query vector computed by transformers.js.
//      This also proves the native <-> wasm storage format is compatible.
//
// Usage:
//   dotnet run --project src/AzureMoe.Chat.Ingest -- embed-dump tools/embed-parity/texts.json tools/embed-parity/dump.json
//   cd tools/embed-parity && npm install
//   node check.mjs dump.json [../../out/blog-XXXX.lbdb] [query text]
//
// The model is loaded from HuggingFace at the dump's pinned revision, the same
// way rag-worker.js loads it in the browser.
import { env, pipeline } from "@huggingface/transformers";
import { createRequire } from "module";
import { readFile } from "fs/promises";
import { resolve, dirname, join, basename } from "path";

const [dumpPath, dbPath, queryText = "Azure Functions の最近のアップデート"] = process.argv.slice(2);
if (!dumpPath) {
  console.error("usage: node check.mjs <dump.json> [db.lbdb] [query]");
  process.exit(1);
}

let failures = 0;
const check = (label, cond, detail = "") => {
  console.log(`${cond ? "PASS" : "FAIL"}: ${label}${detail ? "  " + detail : ""}`);
  if (!cond) failures++;
};
const cosine = (a, b) => {
  let dot = 0, na = 0, nb = 0;
  for (let i = 0; i < a.length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
  return dot / Math.sqrt(na * nb);
};

// ── 1 & 2: token ids + vectors ──────────────────────────────────────────────
const dump = JSON.parse(await readFile(resolve(dumpPath), "utf8"));
console.log(`model: ${dump.modelId}@${dump.revision.slice(0, 7)} [${dump.dtype}]  items: ${dump.items.length}`);

env.allowRemoteModels = true;
const extractor = await pipeline("feature-extraction", dump.modelId, {
  dtype: dump.dtype,
  revision: dump.revision,
  device: "cpu",
});

// Must stay identical to rag-worker.js normalizeQuery().
const normalizeQuery = (t) => t.replace(/\s+/g, " ").trim();

let minCos = 1;
for (const [i, item] of dump.items.entries()) {
  const isQuery = item.kind === "query";
  const input = isQuery ? dump.queryPrefix + normalizeQuery(item.text) : item.input;
  const { input_ids } = await extractor.tokenizer(input);
  const jsIds = Array.from(input_ids.data, Number);
  const idsMatch = JSON.stringify(jsIds) === JSON.stringify(item.ids.map(Number));

  const out = await extractor(input, { pooling: "mean", normalize: true });
  const sim = cosine(Array.from(out.data), item.vector);

  const label = `#${i} ${item.kind} ${JSON.stringify(input.slice(0, 30))}`;
  if (!isQuery) {
    console.log(`INFO: ${label} ids ${idsMatch ? "match" : "differ"}, cosine ${sim.toFixed(6)}`);
    continue;
  }
  minCos = Math.min(minCos, sim);
  check(`${label} input string`, input === item.input);
  check(`${label} token ids`, idsMatch, idsMatch ? `(${jsIds.length} tokens)` : "");
  if (!idsMatch) {
    console.log(`    js:     ${JSON.stringify(jsIds.slice(0, 16))} … (${jsIds.length})`);
    console.log(`    dotnet: ${JSON.stringify(item.ids.slice(0, 16))} … (${item.ids.length})`);
  }
  check(`${label} cosine >= 0.999`, sim >= 0.999, `(${sim.toFixed(6)})`);
}
console.log(`min query cosine: ${minCos.toFixed(6)}`);

// ── 3: built DB opens in wasm and answers a transformers.js query vector ────
if (dbPath) {
  const full = resolve(dbPath);
  const manifestPath = join(dirname(full), "manifest.json");
  let manifest = null;
  try { manifest = JSON.parse(await readFile(manifestPath, "utf8")); } catch { }
  if (manifest && manifest.databaseFile === basename(full)) {
    check("manifest embeddingModel matches dump", manifest.embeddingModel === dump.modelId);
    check("manifest embeddingRevision matches dump", manifest.embeddingRevision === dump.revision);
    check("manifest embeddingDtype matches dump", manifest.embeddingDtype === dump.dtype);
    check("manifest embeddingQueryPrefix matches dump", manifest.embeddingQueryPrefix === dump.queryPrefix);
  } else {
    console.log(`(manifest for ${basename(full)} not found next to the DB — skipping manifest checks)`);
  }

  // Load the same browser sync bundle rag-worker.js uses. Under Node it takes
  // its Node code path, which expects CommonJS globals (require, __dirname)
  // that an ES module doesn't have — provide them before loading it.
  globalThis.require   ??= createRequire(import.meta.url);
  globalThis.__dirname ??= import.meta.dirname;
  const { default: lbug } = await import("@ladybugdb/wasm-core/sync");
  await lbug.init();
  console.log(`wasm engine ${lbug.getVersion()}, storage version ${lbug.getStorageVersion()}`);
  lbug.getFS().createDataFile("/", "chat.db", new Uint8Array(await readFile(full)), true, true, true);
  const db = new lbug.Database("/chat.db");
  const conn = new lbug.Connection(db);

  const run = (cypher, params) => {
    let r;
    if (params) {
      const ps = conn.prepare(cypher);
      if (!ps.isSuccess()) throw new Error(`prepare: ${ps.getErrorMessage()}`);
      r = conn.execute(ps, params);
    } else {
      r = conn.query(cypher);
    }
    if (!r.isSuccess()) { const m = r.getErrorMessage(); r.close(); throw new Error(m); }
    const rows = r.getAllObjects();
    r.close();
    return rows;
  };

  const counts = run("MATCH (c:Chunk) RETURN count(c) AS n");
  check("DB opens and Chunk table is readable", Number(counts[0].n) > 0, `(${counts[0].n} chunks)`);

  // Same query shape and prefix handling as rag-worker.js.
  const qv = Array.from((await extractor(dump.queryPrefix + normalizeQuery(queryText), { pooling: "mean", normalize: true })).data);
  const rows = run(
    `CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_emb_idx', $qv, 5)
     YIELD node AS c, distance
     MATCH (p:Post)-[:HAS_CHUNK]->(c)
     RETURN p.title AS title, distance ORDER BY distance`, { qv });
  check("vector index returns results", rows.length > 0);
  console.log(`query: ${queryText}`);
  for (const r of rows) console.log(`  ${(1 - r.distance).toFixed(3)}  ${r.title}`);

  // The other Cypher shapes rag-worker.js issues, so an engine upgrade that
  // breaks one of them shows up here rather than as a silent warn in the browser.
  const seeds = run(`CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_emb_idx', $qv, 6) YIELD node AS c, distance RETURN c.id AS id`, { qv })
    .map(r => Number(r.id)).join(",");
  const shapes = {
    "schema probe contextText": "MATCH (c:Chunk) RETURN c.contextText AS x LIMIT 1",
    "schema probe ABOUT_SERVICE": "MATCH ()-[e:ABOUT_SERVICE]->() RETURN e LIMIT 1",
    "tag expansion": `MATCH (sp:Post)-[:HAS_CHUNK]->(seed:Chunk) WHERE seed.id IN [${seeds}]
      MATCH (sp)-[:TAGGED]->(t:Tag)<-[:TAGGED]-(p2:Post)-[:HAS_CHUNK]->(c2:Chunk) RETURN c2.id AS cid LIMIT 30`,
    "entity expansion (+RELATED_TO hop)": `MATCH (seed:Chunk)-[:MENTIONS]->(e:Entity) WHERE seed.id IN [${seeds}]
      MATCH (e)-[:RELATED_TO*0..1]-(e2:Entity)<-[:MENTIONS]-(c2:Chunk) RETURN c2.id AS cid LIMIT 30`,
    "service expansion": `MATCH (seed:Chunk)-[:ABOUT_SERVICE]->(s:AzureService)<-[:ABOUT_SERVICE]-(c2:Chunk) WHERE seed.id IN [${seeds}]
      RETURN c2.id AS cid LIMIT 30`,
    "date range scan": `MATCH (p:Post)-[:HAS_CHUNK]->(c:Chunk) WHERE p.date >= '2026-01-01' AND p.date < '2027-01-01'
      RETURN c.id AS cid ORDER BY p.date DESC, c.ordinal ASC LIMIT 10`,
  };
  for (const [name, cypher] of Object.entries(shapes)) {
    try { check(`cypher: ${name}`, true, `(${run(cypher).length} rows)`); }
    catch (e) { check(`cypher: ${name}`, false, e.message); }
  }

  // Date-filtered vector search via a projected graph (vectorSearchInDateRange).
  try {
    run(`CALL PROJECT_GRAPH('dr_check', {'Chunk': 'n.date >= "2000-01-01" AND n.date < "2100-01-01"'}, [])`);
    const n = run(`CALL QUERY_VECTOR_INDEX('dr_check', 'chunk_emb_idx', $qv, 5) YIELD node AS c, distance RETURN c.id AS id`, { qv }).length;
    run(`CALL DROP_PROJECTED_GRAPH('dr_check')`);
    check("cypher: date-filtered vector search (projected graph)", n > 0, `(${n} rows)`);
  } catch (e) { check("cypher: date-filtered vector search (projected graph)", false, e.message); }
}

console.log(failures === 0 ? "\nRESULT: PASS" : `\nRESULT: ${failures} FAILURE(S)`);
process.exit(failures === 0 ? 0 : 1);
