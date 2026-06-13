using System.ClientModel;
using OpenAI;
using OpenAI.Embeddings;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Generates embeddings via any OpenAI-compatible local server
/// (LM Studio, Ollama, llama.cpp, vLLM, …).
///
/// The embedding dimension is auto-detected from the first API response,
/// so no manual configuration is required when switching models.
/// Supports batch requests to reduce round-trips.
/// </summary>
public sealed class LocalLlmEmbedder : IDisposable
{
    private readonly EmbeddingClient _client;

    /// <summary>
    /// Vector dimension, auto-detected from the first embedding response.
    /// Zero until the first call completes.
    /// </summary>
    public int Dimension { get; private set; }

    public LocalLlmEmbedder(string baseUrl, string model, string? apiKey = null)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        var root    = new OpenAIClient(new ApiKeyCredential(apiKey ?? "dummy"), options);
        _client = root.GetEmbeddingClient(model);
    }

    /// <summary>
    /// Embeds a batch of texts in a single API call.
    /// Results are returned in the same order as the input.
    /// </summary>
    public async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        var result     = await _client.GenerateEmbeddingsAsync(texts, cancellationToken: ct);
        var collection = result.Value
            ?? throw new InvalidOperationException("埋め込み API がレスポンスを返しませんでした。");
        var arrays = new float[collection.Count][];
        for (var i = 0; i < collection.Count; i++)
        {
            var floats = collection[i]?.ToFloats()
                ?? throw new InvalidOperationException($"埋め込み [{i}] が null です。");
            arrays[i] = floats.ToArray();
            if (Dimension == 0) Dimension = arrays[i].Length;
        }
        return arrays;
    }

    /// <summary>Embeds a single text (convenience wrapper over <see cref="EmbedBatchAsync"/>).</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => (await EmbedBatchAsync([text], ct))[0];

    public void Dispose() { }
}
