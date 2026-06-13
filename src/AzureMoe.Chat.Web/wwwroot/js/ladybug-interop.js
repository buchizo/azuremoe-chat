// Main-thread side of the LadybugDB bridge.
// Creates a module Web Worker, forwards typed messages, and resolves Promises.

let worker = null;
const pending = new Map();
let _nextId = 1;

function call(type, payload) {
  return new Promise((resolve, reject) => {
    const id = _nextId++;
    pending.set(id, { resolve, reject });
    // Transfer ArrayBuffer ownership to the worker for zero-copy on large DBs.
    const transfer = payload?.dbBytes instanceof ArrayBuffer ? [payload.dbBytes] : [];
    worker.postMessage({ id, type, payload }, transfer);
  });
}

// Create the module worker. workerUrl = absolute URL to ladybug-worker.js.
export function createWorker(workerUrl) {
  if (worker) return;
  worker = new Worker(workerUrl, { type: "module" });
  worker.onerror = (e) => console.error("[ladybug-worker] error:", e);
  worker.onmessage = ({ data: { id, result, error } }) => {
    const entry = pending.get(id);
    if (!entry) return;
    pending.delete(id);
    if (error) entry.reject(new Error(error));
    else entry.resolve(result);
  };
}

// Load DB bytes (Uint8Array from C#) into the worker's WASM FS.
export async function initDb(dbBytes) {
  // dbBytes arrives as a Uint8Array from Blazor; pass its underlying ArrayBuffer.
  return call("init", { dbBytes: dbBytes.buffer });
}

// Run a Cypher query and return { rows: object[] }.
export async function query(cypher) {
  return call("query", { cypher });
}
