// Main-thread bridge to rag-worker.js.
// Mirrors the ladybug-interop.js message protocol but adds support for
// intermediate "progress" messages that do NOT resolve the pending promise.

let worker = null;
let _nextId = 1;
const pending = new Map(); // id → { resolve, reject, onProgress? }

export function createRagWorker(workerUrl) {
  if (worker) return;
  worker = new Worker(workerUrl, { type: "module" });

  worker.onerror = (e) => {
    const msg = `[rag-worker] ${e?.message ?? "worker error"} @ ${e?.filename ?? "?"}:${e?.lineno ?? "?"}`;
    console.error(msg, e);
    for (const [, h] of pending) h.reject(new Error(msg));
    pending.clear();
  };

  worker.onmessage = ({ data: { id, type, payload } }) => {
    const h = pending.get(id);
    if (!h) return;
    if (type === "progress") {
      // Intermediate event — forward to caller but do NOT remove from pending.
      h.onProgress?.(payload);
      return;
    }
    pending.delete(id);
    if (type === "error") h.reject(new Error(payload?.message ?? "rag-worker error"));
    else h.resolve(payload);
  };
}

function send(type, payload, onProgress, transfer) {
  return new Promise((resolve, reject) => {
    const id = _nextId++;
    pending.set(id, { resolve, reject, onProgress });
    worker.postMessage({ id, type, payload }, transfer ?? []);
  });
}

// Initialise DB + embedding model.
// dbBytes arrives as Uint8Array from C#; its underlying ArrayBuffer is
// transferred (zero-copy) to the worker. dotnetRef stays on the main thread —
// DotNetObjectReference cannot be cloned via postMessage, so the worker sends
// plain progress messages back and the onProgress callback invokes dotnetRef here.
// skipEmbedding=true loads the DB only (keyword-search mode for memory-constrained devices).
export async function initRag(dbBytes, embeddingModelId, embeddingDtype, dotnetRef, skipEmbedding = false) {
  return send(
    "init",
    { dbBytes: dbBytes.buffer, embeddingModelId, embeddingDtype, skipEmbedding },
    (p) => dotnetRef.invokeMethodAsync("OnRagProgress", p.stage ?? "", p.file ?? "", p.pct ?? 0),
    [dbBytes.buffer]);
}

// Run the retrieval pipeline. payload = { userQuery, searchQuery, origQ, searchQ, mode, config }.
// Resolves with { chunks: ChunkResult[] }.
export async function retrieveChunks(payload) {
  return send("retrieve", payload);
}

// Terminate the worker and clear in-flight requests. Called by /reload.
// Async: sends a "dispose" message first so the worker can release the
// ONNX WebGPU session before the context is killed. Times out after 1 s
// so a hung or mid-inference worker never stalls /reload indefinitely.
export async function disposeRag() {
  if (!worker) return;
  const w = worker;
  worker = null;  // prevent new send() calls from reaching the old worker

  const id = _nextId++;
  try {
    await Promise.race([
      new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        w.postMessage({ id, type: "dispose", payload: {} });
      }),
      new Promise((_, reject) => setTimeout(() => reject(new Error("timeout")), 1000)),
    ]);
  } catch { /* timeout or error — terminate regardless */ }

  try { w.terminate(); } catch {}
  pending.delete(id);
  pending.clear();
}
