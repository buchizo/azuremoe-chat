namespace AzureMoe.Chat.Web.Services;

public interface IEmbedder
{
    ValueTask LoadAsync(string modelId, IProgress<(string File, int Pct)>? progress = null, CancellationToken ct = default);
    ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default);
}
