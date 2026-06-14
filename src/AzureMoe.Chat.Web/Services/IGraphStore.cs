namespace AzureMoe.Chat.Web.Services;

/// <summary>A chunk returned from the graph, with its source post metadata.
/// <see cref="Distance"/> is the cosine distance for vector hits (0 = identical);
/// for non-vector graph hits it is 0 and callers should rely on their own score.</summary>
public sealed record ChunkResult(
    string Title,
    string Date,
    string Url,
    string Text,
    double Distance,
    long ChunkId = 0);

/// <summary>A chunk reached by graph traversal, with the number of shared
/// connections (shared tags / entities / services) that linked it to the seed.</summary>
public sealed record GraphChunk(ChunkResult Chunk, int Shared);

public interface IGraphStore
{
    ValueTask InitAsync(byte[] dbBytes, CancellationToken ct = default);

    /// <summary>Pure semantic search over the whole corpus.</summary>
    ValueTask<IReadOnlyList<ChunkResult>> VectorSearchAsync(
        float[] queryVec, int topK, CancellationToken ct = default);

    /// <summary>Semantic search restricted to a date window. Over-fetches
    /// <paramref name="overFetch"/> vector neighbours then keeps those whose post
    /// date is in [fromIso, toIso), returning the closest <paramref name="topK"/>.</summary>
    ValueTask<IReadOnlyList<ChunkResult>> VectorSearchInDateRangeAsync(
        float[] queryVec, string fromIso, string toIso, int topK, int overFetch,
        CancellationToken ct = default);

    /// <summary>All chunks whose post date is in [fromIso, toIso), newest first.
    /// Guarantees date coverage even when the vector neighbours miss the window.</summary>
    ValueTask<IReadOnlyList<ChunkResult>> ChunksByDateRangeAsync(
        string fromIso, string toIso, int topK, CancellationToken ct = default);

    /// <summary>Chunks of posts that share tags with the seed chunks' posts.</summary>
    ValueTask<IReadOnlyList<GraphChunk>> ExpandByTagsAsync(
        IReadOnlyList<long> seedChunkIds, int limit, CancellationToken ct = default);

    /// <summary>Chunks that mention the same (or, with <paramref name="includeRelated"/>,
    /// a RELATED_TO-linked) entity as the seed chunks. No-op until the DB is
    /// rebuilt with entity extraction.</summary>
    ValueTask<IReadOnlyList<GraphChunk>> ExpandByEntitiesAsync(
        IReadOnlyList<long> seedChunkIds, int limit, bool includeRelated, CancellationToken ct = default);

    /// <summary>Chunks of posts covering the same Azure service as the seed chunks' posts.</summary>
    ValueTask<IReadOnlyList<GraphChunk>> ExpandByServiceAsync(
        IReadOnlyList<long> seedChunkIds, int limit, CancellationToken ct = default);

    /// <summary>Chunks linked to entities / services / tags whose name matches one
    /// of the query keywords.</summary>
    ValueTask<IReadOnlyList<GraphChunk>> SearchByKeywordsAsync(
        IReadOnlyList<string> keywords, int limit, CancellationToken ct = default);
}
