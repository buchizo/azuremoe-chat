namespace AzureMoe.Chat.Core;

/// <summary>
/// Everything that must be identical between the ingest embedder (ONNX Runtime
/// .NET) and the browser query embedder (transformers.js) for their vectors to
/// share one space. The manifest carries these values so the browser always
/// embeds queries the way the shipped DB's passages were embedded.
/// </summary>
/// <param name="ModelId">HuggingFace repo id (transformers.js layout: tokenizer.json + onnx/*.onnx).</param>
/// <param name="Revision">Commit SHA of <paramref name="ModelId"/>. Pinned so an
/// upstream re-upload can't silently change the query-side model under a built DB.</param>
/// <param name="Dim">Output dimension.</param>
/// <param name="QueryPrefix">Prompt prepended to search queries.</param>
/// <param name="PassagePrefix">Prompt prepended to indexed passages.</param>
/// <param name="BosId">Token id the sequence must start with.</param>
/// <param name="EosId">Token id the sequence must end with.</param>
/// <param name="MaxTokens">Model's maximum sequence length.</param>
/// <param name="License">License shown in notices.</param>
public sealed record EmbeddingProfile(
    string ModelId,
    string Revision,
    int    Dim,
    string QueryPrefix,
    string PassagePrefix,
    long   BosId,
    long   EosId,
    int    MaxTokens,
    string License)
{
    /// <summary>cl-nagoya/ruri-v3-30m (ModernBERT-Ja). JMTEB retrieval 78.08 vs
    /// 67.27 for multilingual-e5-small, at a smaller int8 download (37 MB vs 118 MB).
    /// The onnx-community export lacks tokenizer files, so the sirasagi62 export
    /// (tokenizer included, transformers.js layout) is used.</summary>
    public static readonly EmbeddingProfile RuriV3_30m = new(
        ModelId:       "sirasagi62/ruri-v3-30m-ONNX",
        Revision:      "cdf9391f1ff2198daa8f63f7ccf97d7b3e7415a0",
        Dim:           256,
        QueryPrefix:   "検索クエリ: ",
        PassagePrefix: "検索文書: ",
        BosId:         1,
        EosId:         2,
        MaxTokens:     8192,
        License:       "Apache-2.0");

    /// <summary>Legacy model. Kept so DBs built before the switch (whose
    /// manifests carry no prefix fields) can still be rebuilt or inspected.</summary>
    public static readonly EmbeddingProfile MultilingualE5Small = new(
        ModelId:       "Xenova/multilingual-e5-small",
        Revision:      "761b726dd34fb83930e26aab4e9ac3899aa1fa78",
        Dim:           384,
        QueryPrefix:   "query: ",
        PassagePrefix: "passage: ",
        BosId:         0,
        EosId:         2,
        MaxTokens:     512,
        License:       "MIT");

    public static readonly EmbeddingProfile Default = RuriV3_30m;

    /// <summary>
    /// Collapses whitespace runs (incl. newlines) to one space and trims. Applied
    /// to queries on both sides — rag-worker.js mirrors it as
    /// <c>text.replace(/\s+/g, " ").trim()</c>. transformers.js' Unigram tokenizer
    /// has no byte_fallback, so a newline becomes &lt;unk&gt; in the browser but
    /// &lt;0x0A&gt; in HF tokenizers; without this the two sides tokenize
    /// multi-line queries differently. Passages keep their newlines: they are
    /// only embedded here, with the reference tokenizer.
    /// </summary>
    public static string NormalizeQuery(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    public static IReadOnlyList<EmbeddingProfile> All { get; } = [RuriV3_30m, MultilingualE5Small];

    public static EmbeddingProfile Resolve(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId)
            ? Default
            : All.FirstOrDefault(p => p.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException(
                  $"未知の埋め込みモデル: '{modelId}'。対応モデル: {string.Join(", ", All.Select(p => p.ModelId))}");

    /// <summary>
    /// transformers.js dtype → ONNX file name, mirroring its own mapping so the
    /// ingest side loads exactly the file the browser will. No fallbacks: a
    /// missing file must fail loudly rather than embed passages with a different
    /// quantisation than the queries.
    /// </summary>
    public static string OnnxFileName(string dtype) => dtype.ToLowerInvariant() switch
    {
        "fp32"  => "model.onnx",
        "fp16"  => "model_fp16.onnx",
        "q8"    => "model_quantized.onnx",
        "int8"  => "model_int8.onnx",
        "uint8" => "model_uint8.onnx",
        "q4"    => "model_q4.onnx",
        "q4f16" => "model_q4f16.onnx",
        "bnb4"  => "model_bnb4.onnx",
        _       => throw new ArgumentException($"未対応の dtype: '{dtype}'"),
    };
}
