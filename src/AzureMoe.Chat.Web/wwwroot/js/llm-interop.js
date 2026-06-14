// Main-thread bridge to the LLM Web Worker.
// Uses the same request/response pattern as ladybug-interop.js.
//
// Recovery model: ONNX Runtime Web hosts the WebGPU and WASM execution
// providers in a SINGLE wasm runtime. When the WebGPU device is lost mid-run
// (the "Invalid Buffer / mapAsync / previous error" crash), that runtime is
// poisoned — a WASM reload *inside the same worker* fails the same way. So the
// only reliable recovery is to TERMINATE the worker and spawn a fresh one with
// a clean runtime, then reload the model on WASM. That logic lives here.

let worker = null;
// Map of id → { resolve, reject, onProgress?, onToken? }
const handlers = new Map();
let _nextId = 1;

let _workerUrl  = null;
let _lastLoad   = null;   // { modelId, dtype, dotnetRef } — replayed after a restart
let _forcedWasm = false;  // once we fall to WASM we stay there for this session

function wireWorker() {
  worker.onerror = (e) => console.error("[llm-worker] uncaught:", e);
  worker.onmessage = ({ data: { id, type, payload } }) => {
    const h = handlers.get(id);
    if (!h) return;
    if (type === "progress") { h.onProgress?.(payload); return; }
    if (type === "token")    { h.onToken?.(payload.token); return; }
    handlers.delete(id);
    if (type === "error") h.reject(new Error(payload?.message ?? "Unknown LLM error"));
    else h.resolve(payload);
  };
}

function spawnWorker() {
  worker = new Worker(_workerUrl, { type: "module" });
  wireWorker();
}

// Register a request; intermediate messages (progress/token) go to callbacks,
// final messages (loaded/done/error) resolve or reject the Promise.
function send(type, payload, callbacks = {}) {
  return new Promise((resolve, reject) => {
    const id = _nextId++;
    handlers.set(id, { resolve, reject, ...callbacks });
    worker.postMessage({ id, type, payload });
  });
}

// True when an error means the WebGPU device/runtime is gone and only a fresh
// worker (clean runtime) can recover.
function isGpuLost(message) {
  const msg = String(message ?? "");
  // WebGPU-specific failures that a CPU (WASM) reload can recover from:
  // device loss, invalid/poisoned buffers, and operational limits like
  // unaligned buffer accesses. We deliberately do NOT match deterministic,
  // backend-independent failures here (e.g. "Integer overflow" from an absurd
  // token count) — a worker restart wouldn't help, so let those surface.
  return msg.includes("mapAsync") ||
         msg.includes("Invalid Buffer") ||
         msg.includes("is invalid due to a previous error") ||
         msg.includes("Device lost") ||
         msg.includes("[Device] is lost") ||
         msg.includes("unaligned access") ||
         msg.includes("memory access out of bounds");
}

export function createLlmWorker(workerUrl) {
  if (worker) return;
  _workerUrl = workerUrl;
  spawnWorker();
}

// Load a model. Returns { device: "webgpu" | "wasm" | "built-in" } on success.
export async function loadLlmModel(modelId, dtype, dotnetRef) {
  _lastLoad = { modelId, dtype, dotnetRef };
  return send("load", { modelId, dtype, forceDevice: _forcedWasm ? "wasm" : null }, {
    onProgress: (p) =>
      dotnetRef.invokeMethodAsync("OnLlmProgress", p.file ?? "", p.pct ?? 0),
  });
}

// Tear down the poisoned worker and reload the model on WASM in a fresh one.
async function restartOnWasm() {
  _forcedWasm = true;
  try { worker?.terminate(); } catch { }
  handlers.clear();
  spawnWorker();

  const { modelId, dtype, dotnetRef } = _lastLoad;
  await send("load", { modelId, dtype, forceDevice: "wasm" }, {
    onProgress: (p) =>
      dotnetRef.invokeMethodAsync("OnLlmProgress", p.file ?? "", p.pct ?? 0),
  });
}

// Stream a chat turn. Returns { fullText } when generation is complete.
// If the GPU backend dies, transparently restart on WASM and retry once.
export async function chat(messagesJson, maxNewTokens, dotnetRef) {
  const messages = JSON.parse(messagesJson);
  const run = () => send("generate", { messages, maxNewTokens }, {
    onToken: (token) => dotnetRef.invokeMethodAsync("OnToken", token),
  });

  try {
    return await run();
  } catch (e) {
    // Already on WASM, or not a recoverable GPU failure → surface it.
    if (_forcedWasm || !isGpuLost(e.message)) throw e;

    await dotnetRef.invokeMethodAsync("OnToken",
      "\n⚠ GPU エラーが発生しました。CPU (WASM) に切り替えて読み込み直します...\n\n");
    await restartOnWasm();
    return await run();
  }
}

// Check if Chrome's built-in Prompt API (Gemini Nano) is available on the main thread.
// Called once at startup so the UI can show the right message before the first query.
// Supports both the current API (window.LanguageModel, Chrome 138+) and older builds
// that used window.ai.languageModel.
export async function checkBuiltinAiAvailability() {
  // Current API: top-level LanguageModel (Chrome 138+, no window.ai wrapper)
  if (typeof window.LanguageModel !== "undefined") {
    try {
      const avail = await window.LanguageModel.availability();
      return avail === "available" || avail === "downloadable" || avail === "downloading";
    } catch { }
  }
  // Legacy API: window.ai.languageModel (older Chrome Dev/Canary builds)
  if (typeof window.ai !== "undefined" && window.ai?.languageModel) {
    try {
      const caps = await window.ai.languageModel.capabilities();
      return caps.available === "readily" || caps.available === "after-download";
    } catch { }
  }
  return false;
}
