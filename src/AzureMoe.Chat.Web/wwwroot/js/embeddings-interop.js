// transformers.js interop — query embedding in the browser.
// Uses multilingual-e5-small, the same model as the ingest ONNX side (POC-3 verified).
import { pipeline, env } from "https://esm.sh/@huggingface/transformers@4";

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

// Run the embedding model deterministically on single-threaded WASM CPU.
// On the deployed site the page is cross-origin isolated, so ORT would otherwise
// use multi-threaded WASM (and transformers.js may pick WebGPU). Those paths have
// produced degenerate query vectors (NaN / wrong shape) here, which then overflow
// LadybugDB's vector-index/parser ("Maximum call stack size exceeded"). Embedding
// a short query on CPU is fast, and this matches the local-dev path that works.
env.backends.onnx.wasm.numThreads = 1;

let extractor = null;
let _modelId = null;

// Load the embedding model. Reports progress via dotnetRef callbacks.
export async function loadEmbeddingModel(modelId, dotnetRef) {
  _modelId = modelId;
  // Allow loading from Hugging Face Hub; Cache API is used automatically.
  env.allowRemoteModels = true;
  extractor = await pipeline("feature-extraction", modelId, {
    dtype: "fp32",
    device: "wasm",   // force CPU; avoid WebGPU numerical issues for the query vector
    progress_callback: (info) => {
      if (info?.status === "progress" && dotnetRef) {
        const pct = Math.round((info.loaded ?? 0) / Math.max(info.total ?? 1, 1) * 100);
        dotnetRef.invokeMethodAsync("OnEmbedProgress", info.file ?? "", pct);
      }
    },
  });
  return true;
}

// Release the loaded embedding pipeline (and its tensors), so the next
// loadEmbeddingModel() recreates it from scratch. Used by /reload.
export async function disposeEmbeddingModel() {
  try { await extractor?.dispose?.(); } catch { }
  extractor = null;
  _modelId  = null;
}

// Embed text with the e5 "query: " prefix. Returns number[] (length = model dim, 384 for e5-small).
export async function embedQuery(text) {
  if (!extractor) throw new Error("Embedding model not loaded");
  const out = await extractor("query: " + text, { pooling: "mean", normalize: true });
  const vec = Array.from(out.data);

  // Guard: a degenerate embedding (non-finite values or an unexpected length from a
  // failed mean-pool) makes the Cypher FLOAT[] literal malformed and overflows
  // LadybugDB's parser/vector index ("Maximum call stack size exceeded"). Fail here
  // with a precise message instead, so the cause is unambiguous.
  const badIdx = vec.findIndex((v) => !Number.isFinite(v));
  if (badIdx !== -1) {
    throw new Error(`embedQuery: non-finite value at index ${badIdx} (len=${vec.length}, sample=${vec.slice(0, 4).join(",")})`);
  }
  if (vec.length === 0 || vec.length > 4096) {
    throw new Error(`embedQuery: unexpected vector length ${vec.length} (mean-pool likely failed)`);
  }
  return vec;
}
