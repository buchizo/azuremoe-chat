using AzureMoe.Chat.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Embeds text with a sentence-embedding ONNX model via ONNX Runtime, replicating
/// transformers.js' feature-extraction pipeline (same tokenizer.json, same ONNX
/// file, mean pooling + L2 normalize) so the browser's query vectors land in the
/// same space as the passages built here. Model-specific bits (prefixes, special
/// tokens, dimension) come from the <see cref="EmbeddingProfile"/>.
/// </summary>
public sealed class OnnxEmbedder : IDisposable
{
    private readonly EmbeddingProfile _profile;
    private readonly Tokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly bool _hasTokenTypeIds;

    /// <summary>Embedding dimension detected from the first inference.</summary>
    public int Dimension { get; private set; }

    /// <summary>How many inputs exceeded the model's window and were silently
    /// tail-truncated. Report this after a run — truncation means the tail of the
    /// chunk never made it into the vector.</summary>
    public int TruncatedCount { get; private set; }

    public EmbeddingProfile Profile => _profile;

    /// <summary>Downloads any missing model files (pinned revision), then loads.</summary>
    public static async Task<OnnxEmbedder> CreateAsync(
        string modelDir, EmbeddingProfile profile, string dtype, Action<string> log, CancellationToken ct = default)
    {
        await ModelDownloader.EnsureAsync(modelDir, profile, dtype, log, ct);
        return new OnnxEmbedder(modelDir, profile, dtype);
    }

    public OnnxEmbedder(string modelDir, EmbeddingProfile profile, string dtype)
    {
        _profile = profile;
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        var onnxPath      = Path.Combine(modelDir, "onnx", EmbeddingProfile.OnnxFileName(dtype));

        if (!File.Exists(tokenizerPath) || !File.Exists(onnxPath))
            throw new FileNotFoundException(
                $"埋め込みモデルが見つかりません: '{modelDir}' (dtype={dtype})\n" +
                $"  tokenizer.json と onnx/{Path.GetFileName(onnxPath)} が必要です。\n" +
                $"  取得元: https://huggingface.co/{profile.ModelId} (revision {profile.Revision[..7]})");

        _tokenizer = new Tokenizer(tokenizerPath);
        _session   = new InferenceSession(onnxPath);
        _hasTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public float[] EmbedPassage(string text) => Embed(_profile.PassagePrefix + text);

    public float[] EmbedQuery(string text) => Embed(QueryInput(text));

    /// <summary>The exact string a query is tokenized as (prefix + normalisation).</summary>
    public string QueryInput(string text) => _profile.QueryPrefix + EmbeddingProfile.NormalizeQuery(text);

    /// <summary>Token ids exactly as fed to the model (prefix and special tokens
    /// included). Exposed for the embed-dump parity check against transformers.js.</summary>
    public long[] Tokenize(string textWithPrefix)
    {
        var ids = _tokenizer.Encode(textWithPrefix).Select(id => (long)id).ToList();
        // Tokenizers.DotNet exposes no add_special_tokens switch, so whether the
        // tokenizer.json post-processor already added BOS/EOS varies by model.
        // Normalise to the profile's ids; the parity check guards the result.
        if (ids.Count == 0 || ids[0] != _profile.BosId) ids.Insert(0, _profile.BosId);
        if (ids[^1] != _profile.EosId) ids.Add(_profile.EosId);

        if (ids.Count > _profile.MaxTokens)
        {
            TruncatedCount++;
            ids = ids.Take(_profile.MaxTokens - 1).ToList();
            ids.Add(_profile.EosId);
        }
        return ids.ToArray();
    }

    private float[] Embed(string text)
    {
        var ids = Tokenize(text);
        var n = ids.Length;
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, n])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Ones(n), [1, n])),
        };
        if (_hasTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[n], [1, n])));

        using var outputs = _session.Run(inputs);
        // Same lookup order as transformers.js' feature-extraction pipeline.
        // sentence-transformers exports (e.g. ruri) name it token_embeddings; their
        // pre-pooled sentence_embedding output is ignored because the browser
        // pools token_embeddings itself.
        var hidden = (new[] { "last_hidden_state", "logits", "token_embeddings" }
                .Select(name => outputs.FirstOrDefault(o => o.Name == name))
                .FirstOrDefault(o => o is not null)
            ?? throw new InvalidOperationException(
                $"ONNX 出力にトークン埋め込みがありません: {string.Join(", ", outputs.Select(o => o.Name))}"))
            .AsTensor<float>();
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

    private static long[] Ones(int n)
    {
        var a = new long[n];
        Array.Fill(a, 1L);
        return a;
    }

    public void Dispose() => _session.Dispose();
}
