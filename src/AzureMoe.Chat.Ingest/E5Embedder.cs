using AzureMoe.Chat.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Embeds text with multilingual-e5-small via ONNX Runtime. This is the exact
/// pipeline POC-3 proved matches transformers.js bit-for-bit (mean pooling +
/// L2 normalize, same tokenizer.json, same model.onnx). The browser embeds
/// queries with the identical model, so chunk and query vectors share a space.
/// </summary>
public sealed class E5Embedder : IDisposable
{
    // XLM-RoBERTa special tokens.
    private const long BosId = 0, EosId = 2;

    // multilingual-e5-small (XLM-RoBERTa) の最大シーケンス長
    private const int MaxTokens = 512;

    private readonly Tokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly bool _hasTokenTypeIds;

    /// <summary>Embedding dimension detected from the first inference (384 for multilingual-e5-small).</summary>
    public int Dimension { get; private set; }

    /// <summary>How many inputs exceeded the 512-token window and were silently
    /// tail-truncated. Report this after a run — truncation means the tail of the
    /// chunk never made it into the vector.</summary>
    public int TruncatedCount { get; private set; }

    public E5Embedder(string modelDir, string dtype = "q8")
    {
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        var onnxPath      = ResolveOnnxPath(modelDir, dtype);

        if (!File.Exists(tokenizerPath) || !File.Exists(onnxPath))
            throw new FileNotFoundException(
                $"E5モデルが見つかりません: '{modelDir}' (dtype={dtype})\n" +
                $"  tokenizer.json と onnx/{Path.GetFileName(onnxPath)} が必要です。\n" +
                $"  HuggingFace から Xenova/multilingual-e5-small をダウンロードしてください。");

        _tokenizer = new Tokenizer(tokenizerPath);
        _session   = new InferenceSession(onnxPath);
        _hasTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    /// <summary>Embed a passage (prepends the e5 "passage: " prefix).</summary>
    public float[] EmbedPassage(string text) => Embed(GraphSchema.PassagePrefix + text);

    /// <summary>Embed a query (prepends the e5 "query: " prefix).</summary>
    public float[] EmbedQuery(string text) => Embed(GraphSchema.QueryPrefix + text);

    private float[] Embed(string text)
    {
        var ids = _tokenizer.Encode(text).Select(id => (long)id).ToList();
        if (ids.Count == 0 || ids[0] != BosId) ids.Insert(0, BosId);
        if (ids[^1] != EosId) ids.Add(EosId);

        // モデルの最大シーケンス長 (512) を超える場合は末尾を切り詰め EOS を付け直す
        if (ids.Count > MaxTokens)
        {
            TruncatedCount++;
            ids = ids.Take(MaxTokens - 1).ToList();
            ids.Add(EosId);
        }

        var n = ids.Count;
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids.ToArray(), [1, n])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Ones(n), [1, n])),
        };
        if (_hasTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[n], [1, n])));

        using var outputs = _session.Run(inputs);
        var hidden = outputs.First(o => o.Name == "last_hidden_state").AsTensor<float>();
        var dim = hidden.Dimensions[2];

        var emb = new float[dim];
        for (var t = 0; t < n; t++)
            for (var d = 0; d < dim; d++)
                emb[d] += hidden[0, t, d];
        for (var d = 0; d < dim; d++) emb[d] /= n;          // mean pooling
        var norm = MathF.Sqrt(emb.Sum(v => v * v));
        if (norm > 0) for (var d = 0; d < dim; d++) emb[d] /= norm;   // L2 normalize
        if (Dimension == 0) Dimension = dim;
        return emb;
    }

    private static string ResolveOnnxPath(string modelDir, string dtype)
    {
        var dir = Path.Combine(modelDir, "onnx");
        string[] candidates = dtype.ToLowerInvariant() switch
        {
            "q4" or "int4"  => ["model_q4.onnx",    "model_quantized.onnx", "model.onnx"],
            "q4f16"         => ["model_q4f16.onnx",  "model_q4.onnx",        "model_quantized.onnx", "model.onnx"],
            "fp16"          => ["model_fp16.onnx",   "model.onnx"],
            "fp32"          => ["model.onnx"],
            _               => ["model_quantized.onnx", "model.onnx"],  // q8 / int8 / default
        };
        return candidates
            .Select(f => Path.Combine(dir, f))
            .FirstOrDefault(File.Exists)
            ?? Path.Combine(dir, candidates[^1]);  // let FileNotFoundException surface the exact path
    }

    private static long[] Ones(int n)
    {
        var a = new long[n];
        Array.Fill(a, 1L);
        return a;
    }

    public void Dispose() => _session.Dispose();
}
