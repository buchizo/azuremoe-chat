// POC-1 quick format-compatibility smoke check using the Node.js variant
// (same engine compiled to wasm, host filesystem access). The authoritative
// browser-environment test is browser-test.mjs (Playwright + Chromium).
import { createRequire } from "module";
import { resolve } from "path";
import { copyFile } from "fs/promises";

const require = createRequire(import.meta.url);
const lbug = require("@ladybugdb/wasm-core/nodejs/sync");

const src = resolve(process.argv[2] ?? "../builder/out/poc1.db");
const dbFile = resolve("./poc1-copy.db"); // work on a copy; opening may write (WAL)
await copyFile(src, dbFile);

let failures = 0;
const check = (label, cond) => {
  console.log(`${cond ? "PASS" : "FAIL"}: ${label}`);
  if (!cond) failures++;
};

await lbug.init();
console.log(`wasm engine version: ${lbug.getVersion()}, storage version: ${lbug.getStorageVersion()}`);

const db = new lbug.Database(dbFile);
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

try {
  run("LOAD vector");
  console.log("LOAD vector: ok");
} catch (e) {
  console.log(`LOAD vector failed (may be preloaded): ${e.message}`);
}

const vRows = run(
  "CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_idx', CAST([0.1,0.1,1.0,0.1,0.1,0.1,0.1,0.1] AS FLOAT[8]), 4) " +
  "RETURN node.id AS id, node.text AS text, distance ORDER BY distance");
console.log("vector search results:", JSON.stringify(vRows, null, 2));
const topIds = vRows.slice(0, 2).map(r => Number(r.id)).sort((a, b) => a - b);
check("vector index returns 4 rows", vRows.length === 4);
check("nearest neighbours are ids 2 and 10", topIds[0] === 2 && topIds[1] === 10);

const gRows = run(
  "MATCH (c:Chunk)-[:MENTIONS]->(e:Entity) RETURN e.name AS entity, count(*) AS chunks ORDER BY entity");
console.log("traversal results:", JSON.stringify(gRows));
check("traversal: Azure=8, Cloudflare=8",
  gRows.length === 2 && gRows.every(r => Number(r.chunks) === 8));

conn.close();
db.close();

console.log(failures === 0 ? "\nNODEFS SMOKE: SUCCESS" : `\nNODEFS SMOKE: ${failures} FAILURE(S)`);
process.exit(failures === 0 ? 0 : 1);
