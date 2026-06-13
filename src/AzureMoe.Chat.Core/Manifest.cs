using System.Text.Json.Serialization;

namespace AzureMoe.Chat.Core;

/// <summary>
/// Describes a published database artifact. Uploaded to R2 alongside the DB
/// file; the browser fetches it first to decide whether to (re)download the DB
/// and to verify engine/model compatibility before opening anything.
/// </summary>
public sealed record Manifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("engineVersion")] public required string EngineVersion { get; init; }
    [JsonPropertyName("embeddingModel")] public required string EmbeddingModel { get; init; }
    [JsonPropertyName("embeddingDim")] public required int EmbeddingDim { get; init; }

    /// <summary>Object key of the DB file in the bucket (e.g. "blog-20260613.lbdb").</summary>
    [JsonPropertyName("databaseFile")] public required string DatabaseFile { get; init; }
    [JsonPropertyName("databaseBytes")] public required long DatabaseBytes { get; init; }

    /// <summary>SHA-256 of the DB file, hex. The browser caches by this — a new
    /// hash means re-download; an unchanged hash means serve from IDBFS/OPFS.</summary>
    [JsonPropertyName("databaseSha256")] public required string DatabaseSha256 { get; init; }

    [JsonPropertyName("postCount")] public required int PostCount { get; init; }
    [JsonPropertyName("chunkCount")] public required int ChunkCount { get; init; }
    [JsonPropertyName("entityCount")]  public required int EntityCount  { get; init; }
    [JsonPropertyName("serviceCount")] public int ServiceCount { get; init; }

    /// <summary>UTC ISO-8601. Stamped by the caller (Core stays clock-free for testability).</summary>
    [JsonPropertyName("builtAt")] public required string BuiltAt { get; init; }
}
