namespace AzureMoe.Chat.Core;

/// <summary>A WordPress post reduced to what the graph needs.</summary>
public sealed record Post(long Id, string Title, string Url, string Date, string Html, IReadOnlyList<string> Tags);

/// <summary>A chunk of cleaned post text plus its embedding (filled in later).</summary>
public sealed record Chunk
{
    public required long Id { get; init; }
    public required long PostId { get; init; }
    public required int Ordinal { get; init; }
    public required string Text { get; init; }
    public float[]? Embedding { get; set; }
}

/// <summary>An entity Claude extracted from a chunk.</summary>
public sealed record ExtractedEntity(string Name, string Type, string Description);

/// <summary>A directed relationship between two entities.</summary>
public sealed record ExtractedRelationship(string Source, string Target, string Description);

/// <summary>The structured result Claude returns per chunk.</summary>
public sealed record Extraction(
    IReadOnlyList<ExtractedEntity> Entities,
    IReadOnlyList<ExtractedRelationship> Relationships,
    IReadOnlyList<string>? AzureServices = null);
