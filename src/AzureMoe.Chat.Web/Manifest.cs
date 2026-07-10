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
    [JsonPropertyName("databaseFile")]   public string? DatabaseFile   { get; init; }
    [JsonPropertyName("databaseBytes")]  public long   DatabaseBytes  { get; init; }
    [JsonPropertyName("databaseSha256")] public string? DatabaseSha256 { get; init; }
    [JsonPropertyName("postCount")]      public int    PostCount      { get; init; }
    [JsonPropertyName("chunkCount")]     public int    ChunkCount     { get; init; }
    [JsonPropertyName("entityCount")]    public int    EntityCount    { get; init; }
    [JsonPropertyName("builtAt")]        public string? BuiltAt        { get; init; }
}
