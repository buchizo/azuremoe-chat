namespace AzureMoe.Chat.Web.Services;

public sealed record ChunkResult(
    string Title,
    string Date,
    string Url,
    string Text,
    double Distance);

public interface IGraphStore
{
    ValueTask InitAsync(byte[] dbBytes, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ChunkResult>> VectorSearchAsync(float[] queryVec, int topK, CancellationToken ct = default);
}
