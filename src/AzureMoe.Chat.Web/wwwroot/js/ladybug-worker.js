// Web Worker: wraps @ladybugdb/wasm-core sync build.
// The sync build embeds the WASM binary as a data URL — no separate .wasm fetch needed.
// This module worker is created by ladybug-interop.js from the main thread.
import lbug from "../node_modules/@ladybugdb/wasm-core/sync/index.js";

let conn = null;
let db   = null;

// Convert BigInt values for JSON serialisation.
const plain = (v) => JSON.parse(JSON.stringify(v, (_, val) =>
  typeof val === "bigint" ? Number(val) : val));

self.onmessage = async ({ data: { id, type, payload } }) => {
  try {
    let result;

    if (type === "init") {
      await lbug.init();
      const bytes = new Uint8Array(payload.dbBytes);
      lbug.getFS().createDataFile("/", "chat.db", bytes, true, true, true);
      db   = new lbug.Database("/chat.db");
      conn = new lbug.Connection(db);
      result = { ok: true, version: lbug.getVersion() };

    } else if (type === "query") {
      if (!conn) throw new Error("DB not initialised — call init first");
      const r = conn.query(payload.cypher);
      if (!r.isSuccess()) {
        const msg = r.getErrorMessage();
        r.close();
        throw new Error(msg);
      }
      const rows = plain(r.getAllObjects());
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
