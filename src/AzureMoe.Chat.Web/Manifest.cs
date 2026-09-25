using System.Text.Json.Serialization;

namespace AzureMoe.Chat.Web;

// Browser-side copy of AzureMoe.Chat.Core.Manifest.
// Kept separate so the Web project has no native-binary dependencies.
public sealed record Manifest
{
    [JsonPropertyName("schemaVersion")]  public int    SchemaVersion  { get; init; }
    [JsonPropertyName("engineVersion")]  public string? EngineVersion  { get; init; }
    [JsonPropertyName("embeddingModel")] public string? EmbeddingModel { get; init; }
    [JsonPropertyName("embeddingDim")]   public int    EmbeddingDim   { get; init; }
    [JsonPropertyName("embeddingDtype")] public string? EmbeddingDtype { get; init; }
    [JsonPropertyName("embeddingRevision")]    public string? EmbeddingRevision    { get; init; }
    [JsonPropertyName("embeddingQueryPrefix")] public string? EmbeddingQueryPrefix { get; init; }
    [JsonPropertyName("databaseFile")]   public string? DatabaseFile   { get; init; }
    [JsonPropertyName("databaseBytes")]  public long   DatabaseBytes  { get; init; }
    [JsonPropertyName("databaseSha256")] public string? DatabaseSha256 { get; init; }
    [JsonPropertyName("postCount")]      public int    PostCount      { get; init; }
    [JsonPropertyName("chunkCount")]     public int    ChunkCount     { get; init; }
    [JsonPropertyName("entityCount")]    public int    EntityCount    { get; init; }
    [JsonPropertyName("builtAt")]        public string? BuiltAt        { get; init; }

    /// <summary>
    /// How to embed queries for this DB. The manifest wins over appsettings.json:
    /// passages were embedded with exactly these settings, and anything else puts
    /// queries in a different vector space. Manifests from before the ruri switch
    /// carry no revision/prefix fields; those DBs are multilingual-e5-small
    /// ("query: "), which keeps an old DB working if the web app deploys first.
    /// </summary>
    public EmbeddingSpec ToEmbeddingSpec(AppConfig cfg) => new(
        ModelId:     string.IsNullOrEmpty(EmbeddingModel) ? cfg.EmbeddingModelId : EmbeddingModel,
        Dtype:       string.IsNullOrEmpty(EmbeddingDtype) ? cfg.EmbeddingDtype   : EmbeddingDtype,
        Revision:    string.IsNullOrEmpty(EmbeddingRevision) ? "main" : EmbeddingRevision,
        QueryPrefix: EmbeddingQueryPrefix ?? "query: ");
}

/// <summary>Query-side embedding settings passed to rag-worker.js.</summary>
public sealed record EmbeddingSpec(string ModelId, string Dtype, string Revision, string QueryPrefix);
