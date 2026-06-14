namespace AzureMoe.Chat.Web;

public sealed class AppConfig
{
    public string ManifestUrl { get; set; } = "data/manifest.json";
    public string DbBaseUrl   { get; set; } = "data/";

    // ── LLM ────────────────────────────────────────────────────────────────
    // transformers.js HuggingFace model ID.
    // Use a small Japanese-capable model that fits in browser memory.
    // Recommended options:
    //   "onnx-community/Qwen2.5-0.5B-Instruct"  — ~350 MB q4, very fast
    //   "onnx-community/Qwen2.5-1.5B-Instruct"  — ~900 MB q4, better quality
    // Standard single-file text model (Qwen2ForCausalLM) — the canonical
    // transformers.js demo model. Runs reliably on CPU/WASM (and WebGPU).
    // The previous "Qwen3.5-0.8B-ONNX-OPT" was a 256K-context VL model whose
    // unusual architecture overflowed ONNX Runtime's size calc on every backend.
    public string LlmModelId      { get; set; } = "onnx-community/Qwen2.5-0.5B-Instruct";
    // Decoder dtype. Running CPU (WASM) only — "q4" (4-bit weights, fp32
    // compute) is the best fit for CPU. The worker derives a matching
    // embed_tokens precision in buildDtype() (fp32 here) to avoid overflow.
    public string LlmDtype        { get; set; } = "q4";
    public int    LlmMaxNewTokens { get; set; } = 1024;

    // ── Embedding ──────────────────────────────────────────────────────────
    // Must match the model used during ingest (same vector space).
    public string EmbeddingModelId { get; set; } = "Xenova/multilingual-e5-small";

    // ── RAG ───────────────────────────────────────────────────────────────
    public int    RagTopK         { get; set; } = 5;
    public int    MaxContextChars { get; set; } = 6000;
    public string SystemPrompt    { get; set; } =
        "あなたはAzureやクラウド技術に詳しいアシスタントです。" +
        "提供されたブログ記事の情報をもとに、日本語で丁寧に回答してください。" +
        "情報が不足している場合は、その旨を伝えてください。";
}
