// Main-thread bridge to the LLM Web Worker.
// Uses the same request/response pattern as ladybug-interop.js.

let worker = null;
// Map of id → { resolve, reject, onProgress?, onToken? }
const handlers = new Map();
let _nextId = 1;

// Register a request; intermediate messages (progress/token) go to callbacks,
// final messages (loaded/done/error) resolve or reject the Promise.
function send(type, payload, callbacks = {}) {
  return new Promise((resolve, reject) => {
    const id = _nextId++;
    handlers.set(id, { resolve, reject, ...callbacks });
    worker.postMessage({ id, type, payload });
  });
}

export function createLlmWorker(workerUrl) {
  if (worker) return;
  worker = new Worker(workerUrl, { type: "module" });
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

// Load a model. Returns { device: "webgpu" | "wasm" } on success.
export async function loadLlmModel(modelId, dtype, dotnetRef) {
  return send("load", { modelId, dtype }, {
    onProgress: (p) =>
      dotnetRef.invokeMethodAsync("OnLlmProgress", p.file ?? "", p.pct ?? 0),
  });
}

// Stream a chat turn. Returns { fullText } when generation is complete.
export async function chat(messagesJson, maxNewTokens, dotnetRef) {
  const messages = JSON.parse(messagesJson);
  return send("generate", { messages, maxNewTokens }, {
    onToken: (token) => dotnetRef.invokeMethodAsync("OnToken", token),
  });
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
