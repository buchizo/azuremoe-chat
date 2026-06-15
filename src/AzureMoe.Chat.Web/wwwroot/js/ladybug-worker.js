// Web Worker: wraps @ladybugdb/wasm-core sync build.
// The sync build embeds the WASM binary as a data URL — no separate .wasm fetch needed.
// This module worker is created by ladybug-interop.js from the main thread.
// Imported from wwwroot/lib/ladybug, where the build copies the bundle out of
// node_modules (node_modules is excluded from static web assets, so importing it
// directly 404s on publish). See CopyLadybugRuntime in the .csproj.
import lbug from "../lib/ladybug/index.js";

let conn = null;
let db   = null;

// Convert BigInt values for JSON serialisation.
const plain = (v) => JSON.parse(JSON.stringify(v, (_, val) =>
  typeof val === "bigint" ? Number(val) : val));

self.onmessage = async ({ data: { id, type, payload } }) => {
  try {
    let result;

    if (type === "init") {
      try {
        await lbug.init();
        const bytes = new Uint8Array(payload.dbBytes);
        lbug.getFS().createDataFile("/", "chat.db", bytes, true, true, true);
        db   = new lbug.Database("/chat.db");
        conn = new lbug.Connection(db);
        result = { ok: true, version: lbug.getVersion() };
      } catch (e) {
        throw new Error(`init failed (dbLen=${payload?.dbBytes?.byteLength}): ${e?.message ?? e}`);
      }

    } else if (type === "query") {
      if (!conn) throw new Error("DB not initialised — call init first");
      const cy     = payload.cypher ?? "";
      const params = payload.params ?? null;   // e.g. { qv: [...] } for vector queries

      let r;
      try {
        if (params) {
          // Parameterised query: used for vector searches ($qv) so the float array
          // travels as data rather than being inlined into the Cypher string.
          const ps = conn.prepare(cy);
          if (!ps.isSuccess()) {
            const em = ps.getErrorMessage();
            ps.close?.();
            throw new Error(`prepare failed: ${em}`);
          }
          r = conn.execute(ps, params);
          ps.close?.();
        } else {
          r = conn.query(cy);
        }
      } catch (e) {
        throw new Error(`query() threw: ${e?.message ?? e}`);
      }

      if (!r.isSuccess()) {
        const msg = r.getErrorMessage();
        r.close();
        throw new Error(`cypher error: ${msg}`);
      }

      let rows;
      try {
        rows = plain(r.getAllObjects());
      } catch (e) {
        r.close();
        throw new Error(`result parse threw: ${e?.message ?? e}`);
      }
      r.close();
      result = { rows };

    } else {
      throw new Error(`Unknown message type: ${type}`);
    }

    self.postMessage({ id, result });
  } catch (e) {
    self.postMessage({ id, error: String(e?.message ?? e) });
  }
};
