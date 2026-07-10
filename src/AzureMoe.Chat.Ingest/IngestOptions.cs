namespace AzureMoe.Chat.Ingest;

/// <summary>
/// All ingest configuration. Bound from (in priority order) command line,
/// environment variables, then appsettings.json. Secrets (API keys)
/// should come from env vars, never appsettings.
/// </summary>
public sealed class IngestOptions
{
    /// <summary>
    /// Directory that contains WordPress export XML (.xml) files.
    /// Default: .tmp  (place WXR export files here before running).
    /// </summary>
    public string XmlDir { get; set; } = ".tmp";

    /// <summary>Max posts to process. 0 = all.</summary>
    public int MaxPosts { get; set; }

    // --- Local LLM (OpenAI-compatible) -------------------------------------

    /// <summary>
    /// Base URL of the OpenAI-compatible local LLM endpoint.
    /// Examples:
    ///   Ollama  : http://localhost:11434/v1
    ///   LM Studio: http://localhost:1234/v1
    ///   llama.cpp: http://localhost:8080/v1
    /// Env: LLM_BASE_URL
    /// </summary>
    public string LlmBaseUrl { get; set; } = "http://localhost:11434/v1";

    /// <summary>
    /// Model name passed to the local LLM endpoint.
    /// Examples: "qwen3:8b", "llama3.1:8b", "gemma3:12b"
    /// Env: LLM_MODEL
    /// </summary>
    public string LlmModel { get; set; } = "qwen3:8b";

    /// <summary>
    /// API key for the LLM endpoint. Leave empty for unauthenticated local servers.
    /// Env: LLM_API_KEY
    /// </summary>
    public string? LlmApiKey { get; set; }

    /// <summary>Skip the LLM extraction step; build the graph from tags/structure only.
    /// Useful for testing the rest of the pipeline without a running LLM.</summary>
    public bool NoLlm { get; set; }

    // --- Embeddings (multilingual-e5-small, ONNX local) -------------------

    /// <summary>
    /// Directory containing the Xenova/multilingual-e5-small ONNX model files.
    /// Expected layout:
    ///   {ModelDir}/tokenizer.json
    ///   {ModelDir}/onnx/model_quantized.onnx  (q8/INT8, ~34 MB, default)
    ///   {ModelDir}/onnx/model_q4.onnx          (q4/INT4, ~17 MB, if available)
    ///   {ModelDir}/onnx/model.onnx             (fp32, ~117 MB)
    ///
    /// Download from HuggingFace: Xenova/multilingual-e5-small
    /// </summary>
    public string ModelDir { get; set; } = "model/Xenova/multilingual-e5-small";

    /// <summary>
    /// ONNX quantization dtype. Must match the browser-side EmbeddingDtype in
    /// appsettings.json so query and passage vectors share the same space.
    /// Supported: "q8" (INT8, default), "q4" (INT4), "fp16", "fp32".
    /// </summary>
    public string EmbeddingDtype { get; set; } = "q4";

    // --- Output ------------------------------------------------------------

    /// <summary>Output directory for the built DB and manifest.</summary>
    public string OutDir { get; set; } = "out";
}
