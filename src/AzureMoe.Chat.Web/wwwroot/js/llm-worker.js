// Web Worker: LLM pipeline.
// Priority: 1) Chrome built-in AI (Gemini Nano, no model download)
//           2) transformers.js on WebGPU
//           3) transformers.js on WASM CPU
// transformers.js v4+ required — Qwen3.5 (qwen3_5) support was added in v4.
// Pinned to an exact version (not the floating @4 range) so esm.sh always
// resolves the same build — 4.2.0 is the current latest. NOTE: the WebGPU
// "Invalid Buffer / mapAsync" crash on repeated generation lives in v4's
// native (C++) WebGPU runtime and is not fixed in any 4.x release, so a
// version bump alone will not resolve it; we mitigate via the WASM fallback
// and by keeping GPU buffers small (trimmed history + bounded max tokens).
import { env, pipeline, TextStreamer } from "https://esm.sh/@huggingface/transformers@4.2.0";

// In production the page is cross-origin isolated (coi-serviceworker sets COEP
// for SharedArrayBuffer), which blocks direct cross-origin downloads from
// huggingface.co with a CORS error. Route model fetches through our same-origin
// Worker proxy (/hf/* → huggingface.co) so they bypass CORS/COEP. Local dev
// talks to HF directly — the /hf route only exists on the deployed Worker.
const isLocalDev = self.location.hostname === "localhost" || self.location.hostname === "127.0.0.1";
if (!isLocalDev) {
  env.remoteHost = self.location.origin;
  env.remotePathTemplate = "hf/{model}/resolve/{revision}/";
}

// Multi-threaded WASM needs SharedArrayBuffer, which requires cross-origin
// isolation (provided by coi-serviceworker.js). When isolated, use most of the
// CPU cores; otherwise ORT falls back to single-threaded (much slower).
if (self.crossOriginIsolated) {
  const cores = (typeof navigator !== "undefined" && navigator.hardwareConcurrency) || 4;
  env.backends.onnx.wasm.numThreads = Math.max(1, Math.min(cores - 1, 8));
} else {
  env.backends.onnx.wasm.numThreads = 1;
}

// Use WebGPU when the environment supports it, else fall back to CPU (WASM).
// detectDevice() picks WebGPU only if navigator.gpu yields an adapter, so
// non-capable environments transparently get WASM. If WebGPU dies at runtime,
// llm-interop.js restarts the worker on WASM automatically. The current model
// (LFM2.5-1.2B-JP-ONNX) is built for ONNX Runtime Web + WebGPU.
const ENABLE_WEBGPU = true;

// Chrome's built-in Gemini Nano is a DIFFERENT model than the configured one.
// Disabled so the configured LLM is always what runs.
const ENABLE_BUILTIN_AI = false;

let pipe         = null;
let useBuiltinAI = false;
let activeDevice = "wasm";
let loadedModelId = null;
let loadedDtype   = "q4";

// Set by an "interrupt" message; checked each streamed token to abort the
// current generation (emergency stop). Sentinel used so the main thread can
// tell a user stop apart from a real error.
let interrupted = false;
const INTERRUPT_SENTINEL = "__INTERRUPTED__";

async function detectDevice() {
  if (!ENABLE_WEBGPU) return "wasm";
  if (typeof navigator === "undefined" || !navigator.gpu) return "wasm";
  try {
    const adapter = await navigator.gpu.requestAdapter();
    return adapter ? "webgpu" : "wasm";
  } catch {
    return "wasm";
  }
}

function getLanguageModelAPI() {
  if (typeof self.LanguageModel !== "undefined") return self.LanguageModel;
  if (typeof self.ai !== "undefined" && self.ai?.languageModel) return self.ai.languageModel;
  return null;
}

async function checkBuiltinAI() {
  const api = getLanguageModelAPI();
  if (!api) return false;
  try {
    if (typeof api.availability === "function") {
      const avail = await api.availability();
      return avail === "available" || avail === "downloadable" || avail === "downloading";
    }
    if (typeof api.capabilities === "function") {
      const caps = await api.capabilities();
      return caps.available === "readily" || caps.available === "after-download";
    }
    return false;
  } catch {
    return false;
  }
}

// WebGPU device crashed or became invalid during inference.
function isGpuDeviceLost(err) {
  const msg = String(err?.message ?? err);
  return msg.includes("[Device] is lost") ||
         msg.includes("Device lost") ||
         msg.includes("OrtRun") ||
         msg.includes("mapAsync") ||
         msg.includes("is invalid due to a previous error") ||
         msg.includes("Invalid Buffer");
}

// ONNX operator not available on the chosen backend/dtype.
// GatherBlockQuantized is used by q4 block-quantized models; it is NOT
// supported on WebGPU and may be absent on some WASM builds.
function isOperatorUnsupported(err) {
  const msg = String(err?.message ?? err);
  return msg.includes("GatherBlockQuantized") ||
         msg.includes("Could not find an implementation") ||
         msg.includes("NOT_IMPLEMENTED");
}

// Try to load a pipeline, falling back to null task-auto-detect for VL models.
async function tryPipeline(modelId, opts) {
  try {
    return await pipeline("text-generation", modelId, opts);
  } catch (e) {
    if (String(e).includes("does not support")) {
      return await pipeline(null, modelId, opts);
    }
    throw e;
  }
}


self.onmessage = async ({ data: { id, type, payload } }) => {
  // ── interrupt ───────────────────────────────────────────────────────────
  // Out-of-band stop signal. Processed between token generations (the async
  // generation loop yields to the event loop), so the next streamed token
  // aborts the run. No response is sent.
  if (type === "interrupt") { interrupted = true; return; }

  try {
    // ── load ──────────────────────────────────────────────────────────────
    if (type === "load") {
      // forceDevice is set by the main thread when recovering from a lost GPU
      // ("wasm") — skip device auto-detection and the GPU-backed paths entirely.
      const { modelId, dtype = "q4", forceDevice = null } = payload;
      loadedModelId = modelId;
      loadedDtype   = dtype;

      // 1. Chrome built-in AI (Gemini Nano) — no external model download.
      //    Disabled (ENABLE_BUILTIN_AI) so the configured model always runs.
      if (ENABLE_BUILTIN_AI && forceDevice !== "wasm" && await checkBuiltinAI()) {
        useBuiltinAI = true;
        activeDevice = "built-in";
        const llmApi = getLanguageModelAPI();
        const initSession = await llmApi.create({
          monitor(m) {
            m.addEventListener("downloadprogress", (e) => {
              const pct = e.total > 0 ? Math.round(e.loaded / e.total * 100) : 0;
              self.postMessage({ id, type: "progress", payload: { file: "Gemini Nano", pct } });
            });
          },
        });
        initSession.destroy();
        self.postMessage({ id, type: "loaded", payload: { device: activeDevice } });
        return;
      }

      // 2. transformers.js — waterfall of (device, dtype) strategies.
      //
      // q4 block-quantized models use GatherBlockQuantized which is NOT
      // supported on WebGPU and may be absent on some WASM builds.
      // We fall through strategies until one succeeds.
      useBuiltinAI  = false;
      const detected = forceDevice ?? await detectDevice();
      activeDevice   = detected;

      const progressCb = (info) => {
        if (info.status === "progress") {
          const pct = info.total > 0 ? Math.round(info.loaded / info.total * 100) : 0;
          self.postMessage({ id, type: "progress", payload: { file: info.file ?? "", pct } });
        }
      };

      // Strategy list: webgpu/dtype → wasm/q8 → wasm/fp16
      // NOTE: q4 / q4f16 use GatherBlockQuantized, which the WASM execution
      // provider does NOT implement (WebGPU/WebNN only). So we must never put
      // a block-quantized dtype on a WASM strategy — that produces the
      // "Could not find an implementation for GatherBlockQuantized" error.
      const isBlockQuant = (dt) => dt === "q4" || dt === "q4f16";
      const strategies = [];
      strategies.push({ device: detected, dtype });
      if (detected === "webgpu" && !isBlockQuant(dtype))
        strategies.push({ device: "wasm", dtype });
      if (dtype !== "q8")  strategies.push({ device: "wasm", dtype: "q8" });
      if (dtype !== "fp16") strategies.push({ device: "wasm", dtype: "fp16" });

      let lastError = null;
      for (let i = 0; i < strategies.length; i++) {
        const s = strategies[i];
        try {
          const opts = { dtype: s.dtype, device: s.device, progress_callback: progressCb };
          pipe         = await tryPipeline(modelId, opts);
          activeDevice = s.device;
          loadedDtype  = s.dtype;
          lastError    = null;
          break;
        } catch (e) {
          lastError = e;
          if (isOperatorUnsupported(e) || isGpuDeviceLost(e)) {
            if (i + 1 < strategies.length) {
              const next = strategies[i + 1];
              self.postMessage({ id, type: "progress", payload: {
                file: `${s.device}/${s.dtype} 不可 → ${next.device}/${next.dtype} で再試行`, pct: 0,
              }});
            }
            continue;
          }
          throw e;
        }
      }
      if (lastError) throw lastError;

      self.postMessage({ id, type: "loaded", payload: { device: activeDevice } });

    // ── generate ──────────────────────────────────────────────────────────
    } else if (type === "generate") {
      if (!useBuiltinAI && !pipe) throw new Error("Model not loaded — call 'load' first");
      const { messages, maxNewTokens = 1024 } = payload;
      let fullText = "";
      interrupted = false;   // fresh run

      if (useBuiltinAI) {
        const llmApi    = getLanguageModelAPI();
        const systemMsg = messages.find(m => m.role === "system");
        const nonSystem = messages.filter(m => m.role !== "system");
        const lastUser  = nonSystem.at(-1);
        const history   = nonSystem.slice(0, -1);

        const session = await llmApi.create({
          systemPrompt:   systemMsg?.content ?? "",
          initialPrompts: history.map(m => ({ role: m.role, content: m.content })),
        });
        try {
          const stream = session.promptStreaming(lastUser?.content ?? "");
          let prev = "";
          for await (const chunk of stream) {
            if (interrupted) throw new Error(INTERRUPT_SENTINEL);
            const delta = chunk.slice(prev.length);
            prev = chunk;
            if (delta) {
              fullText += delta;
              self.postMessage({ id, type: "token", payload: { token: delta } });
            }
          }
        } finally {
          session.destroy();
        }

      } else {
        const streamer = new TextStreamer(pipe.tokenizer, {
          skip_prompt: true,
          skip_special_tokens: true,
          callback_function: (chunk) => {
            fullText += chunk;
            self.postMessage({ id, type: "token", payload: { token: chunk } });
            // Throwing here aborts the in-progress generate() — this is how the
            // emergency stop halts the model mid-run.
            if (interrupted) throw new Error(INTERRUPT_SENTINEL);
          },
        });
        // Run inference. If the WebGPU backend dies here (Invalid Buffer /
        // mapAsync / device lost), we do NOT try to recover in this worker —
        // the shared ORT runtime is poisoned and an in-process WASM reload
        // fails the same way. We let the error propagate to the main thread,
        // which terminates this worker and reloads on WASM in a fresh one.
        await pipe(messages, {
          max_new_tokens: maxNewTokens,
          // Light sampling instead of greedy decoding. Greedy on a small (1.2B)
          // model degenerates into repeated words / runaway bullet lists; mild
          // sampling + a repetition penalty keeps output coherent and natural.
          do_sample: true,
          temperature: 0.4,
          top_p: 0.9,
          top_k: 40,
          repetition_penalty: 1.2,
          no_repeat_ngram_size: 3,
          streamer,
          return_full_text: false,
        });
      }

      self.postMessage({ id, type: "done", payload: { fullText } });

    } else {
      throw new Error(`Unknown message type: ${type}`);
    }
  } catch (e) {
    self.postMessage({ id, type: "error", payload: { message: String(e?.message ?? e) } });
  }
};
