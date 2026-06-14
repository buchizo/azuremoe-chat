namespace AzureMoe.Chat.Core;

/// <summary>
/// The single source of truth for the GraphRAG schema, shared by the ingest
/// tool (native Ladybug) and the browser app (wasm Ladybug). The two engines
/// must stay on the same Ladybug major.minor version or the database file
/// won't open — see <see cref="EngineVersion"/>.
/// </summary>
public static class GraphSchema
{
    /// <summary>Ladybug engine version the DB file is built with. Must match the
    /// browser's @ladybugdb/wasm-core version (storage format compatibility).</summary>
    public const string EngineVersion = "0.17.0";

    /// <summary>Embedding dimension of multilingual-e5-small.</summary>
    public const int EmbeddingDim = 384;

    /// <summary>Embedding model id (same ONNX on both sides — see POC-3).</summary>
    public const string EmbeddingModel = "Xenova/multilingual-e5-small";

    // e5 requires these prefixes; queries and passages live in different "modes".
    public const string QueryPrefix = "query: ";
    public const string PassagePrefix = "passage: ";

    /// <summary>
    /// DDL run once when building a fresh database. Order matters: node tables
    /// before the rel tables that reference them. The vector index is created
    /// separately after the chunks are loaded (HNSW build is cheaper in bulk).
    /// </summary>
    /// <summary>
    /// Returns the DDL statements for a given embedding dimension.
    /// Use this overload when the dimension comes from a runtime-configured
    /// embedding model (e.g. a local LLM endpoint).
    /// </summary>
    public static IReadOnlyList<string> GetSchemaDdl(int embeddingDim) =>
    [
        "CREATE NODE TABLE Post(id INT64, title STRING, url STRING, date STRING, year INT64, month INT64, PRIMARY KEY(id))",
        // date/title/year/month are denormalised from the owning Post so chunk-level
        // queries (date filtering, display) need no join back to Post.
        $"CREATE NODE TABLE Chunk(id INT64, postId INT64, ordinal INT64, text STRING, date STRING, title STRING, year INT64, month INT64, emb FLOAT[{embeddingDim}], PRIMARY KEY(id))",
        "CREATE NODE TABLE Entity(name STRING, type STRING, description STRING, PRIMARY KEY(name))",
        "CREATE NODE TABLE AzureService(name STRING, PRIMARY KEY(name))",
        "CREATE NODE TABLE Tag(name STRING, PRIMARY KEY(name))",
        "CREATE REL TABLE HAS_CHUNK(FROM Post TO Chunk)",
        "CREATE REL TABLE MENTIONS(FROM Chunk TO Entity)",
        "CREATE REL TABLE RELATED_TO(FROM Entity TO Entity, description STRING)",
        "CREATE REL TABLE TAGGED(FROM Post TO Tag)",
        "CREATE REL TABLE COVERS_SERVICE(FROM Post TO AzureService)",
    ];

    /// <summary>DDL using the default E5 embedding dimension (384).</summary>
    public static IReadOnlyList<string> SchemaDdl => GetSchemaDdl(EmbeddingDim);

    /// <summary>Extract (year, month) from an ISO-8601 date string ("2026-02-25T…").
    /// Returns (0, 0) when the prefix isn't a parseable yyyy-MM.</summary>
    public static (int Year, int Month) ParseYearMonth(string? isoDate)
    {
        if (isoDate is { Length: >= 7 }
            && int.TryParse(isoDate.AsSpan(0, 4), out var y)
            && isoDate[4] == '-'
            && int.TryParse(isoDate.AsSpan(5, 2), out var m)
            && m is >= 1 and <= 12)
            return (y, m);
        return (0, 0);
    }

    public const string VectorIndexName = "chunk_emb_idx";

    /// <summary>Creates the cosine HNSW index over Chunk.emb. Native engine only;
    /// the index persists in the file and is queried directly in the browser.</summary>
    public static string CreateVectorIndexCypher =>
        $"CALL CREATE_VECTOR_INDEX('Chunk', '{VectorIndexName}', 'emb', metric := 'cosine')";
}
