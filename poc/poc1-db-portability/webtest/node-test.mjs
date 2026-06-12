// POC-1 wasm-side test.
// Uses the sync flavour of the DEFAULT (browser) variant of @ladybugdb/wasm-core —
// the same single-threaded wasm engine a browser would run (the async flavour only
// differs by dispatching to a Web Worker, which Node cannot construct).
// Verifies that a database built by the native ladybug-dotnet engine opens in
// the wasm engine, and that its vector index and graph traversal both work.
import lbug from "@ladybugdb/wasm-core/sync";
import { readFile } from "fs/promises";
import { resolve } from "path";

const dbFile = resolve(process.argv[2] ?? "../builder/out/poc1.db");
let failures = 0;

const check = (label, cond) => {
  console.log(`${cond ? "PASS" : "FAIL"}: ${label}`);
  if (!cond) failures++;
};

await lbug.init();
console.log(`wasm engine version: ${lbug.getVersion()}, storage version: ${lbug.getStorageVersion()}`);

const bytes = await readFile(dbFile);
console.log(`Loaded ${dbFile} (${bytes.length} bytes), writing into wasm FS...`);
lbug.getFS().writeFile("/poc1.db", new Uint8Array(bytes));

const db = new lbug.Database("/poc1.db");
const conn = new lbug.Connection(db);

const run = (statement) => {
  const result = conn.query(statement);
  if (!result.isSuccess()) {
    const message = result.getErrorMessage();
    result.close();
    throw new Error(`query failed: ${message}\n  ${statement}`);
  }
  const rows = result.getAllObjects();
  result.close();
  return rows;
};

// Extensions are statically linked into the wasm build; LOAD activates them.
try {
  run("LOAD vector");
  console.log("LOAD vector: ok");
} catch (e) {
  console.log(`LOAD vector failed (may be preloaded): ${e.message}`);
}

// 1. Vector search via the index built on desktop (query axis 2 → ids 2, 10)
const vRows = run(
  "CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_idx', CAST([0.1,0.1,1.0,0.1,0.1,0.1,0.1,0.1] AS FLOAT[8]), 4) " +
  "RETURN node.id AS id, node.text AS text, distance ORDER BY distance");
console.log("vector search results:", JSON.stringify(vRows, null, 2));
const topIds = vRows.slice(0, 2).map(r => Number(r.id)).sort((a, b) => a - b);
check("vector index returns 4 rows", vRows.length === 4);
check("nearest neighbours are ids 2 and 10", topIds[0] === 2 && topIds[1] === 10);
check("results include Japanese text", String(vRows[0]?.text).includes("チャンク"));

// 2. Graph traversal over rel table
const gRows = run(
  "MATCH (c:Chunk)-[:MENTIONS]->(e:Entity) RETURN e.name AS entity, count(*) AS chunks ORDER BY entity");
console.log("traversal results:", JSON.stringify(gRows));
check("two entities found", gRows.length === 2);
check("Azure has 8 chunks", gRows.some(r => r.entity === "Azure" && Number(r.chunks) === 8));
check("Cloudflare has 8 chunks", gRows.some(r => r.entity === "Cloudflare" && Number(r.chunks) === 8));

// 3. Vector + graph combined (the GraphRAG core pattern)
const cRows = run(
  "CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_idx', CAST([0.1,0.1,1.0,0.1,0.1,0.1,0.1,0.1] AS FLOAT[8]), 2) " +
  "WITH node AS c, distance MATCH (c)-[:MENTIONS]->(e:Entity) " +
  "RETURN c.id AS id, e.name AS entity ORDER BY id");
console.log("vector+graph results:", JSON.stringify(cRows));
check("vector hits expand to entities via graph", cRows.length === 2 && cRows.every(r => r.entity === "Azure"));

conn.close();
db.close();

console.log(failures === 0 ? "\nPOC-1 RESULT: SUCCESS" : `\nPOC-1 RESULT: ${failures} FAILURE(S)`);
process.exit(failures === 0 ? 0 : 1);
