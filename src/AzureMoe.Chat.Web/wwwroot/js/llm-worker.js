// Web Worker: LLM pipeline.
// Priority: 1) Chrome built-in AI (Gemini Nano, no model download)
//           2) transformers.js on WebGPU
//           3) transformers.js on WASM CPU
// transformers.js v4+ required — Qwen3.5 (qwen3_5) support was added in v4.
import { pipeline, TextStreamer } from "https://esm.sh/@huggingface/transformers@4";

let pipe         = null;   // transformers.js pipeline (null when using built-in AI)
let useBuiltinAI = false;
let activeDevice = "wasm";

// Saved at load time so the generate handler can reload on WASM fallback.
let loadedModelId = null;
let loadedDtype   = "q4";

async function detectDevice() {
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

// ONNX operator not available on the chosen backend/dtype combination.
// GatherBlockQuantized is used by q4 block-quantized models and is NOT
// supported on WebGPU; it may also be absent on older WASM builds.
function isOperatorUnsupported(err) {
  const msg = String(err?.message ?? err);
  return msg.includes("GatherBlockQuantized") ||
         msg.includes("Could not find an implementation") ||
         msg.includes("NOT_IMPLEMENTED");
}

self.onmessage = async ({ data: { id, type, payload } }) => {
  try {
    // ── load ──────────────────────────────────────────────────────────────
    if (type === "load") {
      const { modelId, dtype = "q4" } = payload;
      loadedModelId = modelId;
      loadedDtype   = dtype;

      // 1. Chrome built-in AI (Gemini Nano) — no external model download
      if (await checkBuiltinAI()) {
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
      // Some quantization formats (e.g. q4 block-quantized) use the
      // GatherBlockQuantized ONNX operator which is unsupported on WebGPU
      // and may be absent on some WASM builds.  We fall through strategies
      // until one works, reporting each retry as a progress message.
      //
      // Strategies tried in order:
      //   a) detected device   + configured dtype
      //   b) wasm              + configured dtype   (if a used webgpu)
      //   c) wasm              + "q8"               (avoids GatherBlockQuantized)
      useBuiltinAI  = false;
      const detected = await detectDevice();
      activeDevice   = detected;

      const progressCallback = (info) => {
        if (info.status === "progress") {
          const pct = info.total > 0 ? Math.round(info.loaded / info.total * 100) : 0;
          self.postMessage({ id, type: "progress", payload: { file: info.file ?? "", pct } });
        }
      };

      // Build the strategy list
      const strategies = [];
      strategies.push({ device: detected, dtype });
      if (detected === "webgpu") strategies.push({ device: "wasm", dtype });
      if (dtype !== "q8") strategies.push({ device: "wasm", dtype: "q8" });

      let lastError = null;
      for (const s of strategies) {
        try {
          const opts = { dtype: s.dtype, device: s.device, progress_callback: progressCallback };
          let p;
          try {
            p = await pipeline("text-generation", modelId, opts);
          } catch (e) {
            if (String(e).includes("does not support")) {
              p = await pipeline(null, modelId, opts);
            } else {
              throw e;
            }
          }
          pipe         = p;
          activeDevice = s.device;
          loadedDtype  = s.dtype;
          lastError    = null;
          break;
        } catch (e) {
          lastError = e;
          if (isOperatorUnsupported(e) || isGpuDeviceLost(e)) {
            // Recoverable: log and try next strategy
            const next = strategies[strategies.indexOf(s) + 1];
            if (next) {
              self.postMessage({ id, type: "progress", payload: {
                file: `${s.device}/${s.dtype} 不可 → ${next.device}/${next.dtype} で再試行`, pct: 0,
              }});
            }
            continue;
          }
          throw e;  // unrecoverable (e.g. network error, wrong model ID)
        }
      }
      if (lastError) throw lastError;

      self.postMessage({ id, type: "loaded", payload: { device: activeDevice } });

    // ── generate ──────────────────────────────────────────────────────────
    } else if (type === "generate") {
      if (!useBuiltinAI && !pipe) throw new Error("Model not loaded — call 'load' first");
      const { messages, maxNewTokens = 1024 } = payload;
      let fullText = "";

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
        const runInference = async (currentPipe) => {
          const streamer = new TextStreamer(currentPipe.tokenizer, {
            skip_prompt: true,
            skip_special_tokens: true,
            callback_function: (chunk) => {
              fullText += chunk;
              self.postMessage({ id, type: "token", payload: { token: chunk } });
            },
          });
          await currentPipe(messages, {
            max_new_tokens: maxNewTokens,
            do_sample: false,
            repetition_penalty: 1.1,
            streamer,
            return_full_text: false,
          });
        };

        try {
          await runInference(pipe);
        } catch (e) {
          // WebGPU device lost during inference — reload pipeline on WASM CPU and retry.
          if (isGpuDeviceLost(e) && activeDevice === "webgpu" && loadedModelId) {
            fullText = "";
            self.postMessage({ id, type: "token", payload: {
              token: "⚠ WebGPU エラーが発生しました。CPU (WASM) に切り替えて再試行します...\n\n",
            }});
            activeDevice = "wasm";
            pipe = await pipeline("text-generation", loadedModelId, {
              dtype: loadedDtype,
              device: "wasm",
            });
            fullText = "";
            await runInference(pipe);
          } else {
            throw e;
          }
        }
      }

      self.postMessage({ id, type: "done", payload: { fullText } });

    } else {
      throw new Error(`Unknown message type: ${type}`);
    }
  } catch (e) {
    self.postMessage({ id, type: "error", payload: { message: String(e?.message ?? e) } });
  }
};
