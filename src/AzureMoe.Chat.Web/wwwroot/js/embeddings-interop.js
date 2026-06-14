// transformers.js interop — query embedding in the browser.
// Uses multilingual-e5-small, the same model as the ingest ONNX side (POC-3 verified).
import { pipeline, env } from "https://esm.sh/@huggingface/transformers@4";

let extractor = null;
let _modelId = null;

// Load the embedding model. Reports progress via dotnetRef callbacks.
export async function loadEmbeddingModel(modelId, dotnetRef) {
  _modelId = modelId;
  // Allow loading from Hugging Face Hub; Cache API is used automatically.
  env.allowRemoteModels = true;
  extractor = await pipeline("feature-extraction", modelId, {
    dtype: "fp32",
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

// Embed text with the e5 "query: " prefix. Returns Float32Array.
export async function embedQuery(text) {
  if (!extractor) throw new Error("Embedding model not loaded");
  const out = await extractor("query: " + text, { pooling: "mean", normalize: true });
  return Array.from(out.data);
}
