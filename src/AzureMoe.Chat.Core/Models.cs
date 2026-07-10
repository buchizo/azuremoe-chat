namespace AzureMoe.Chat.Core;

/// <summary>A WordPress post reduced to what the graph needs.</summary>
public sealed record Post(long Id, string Title, string Url, string Date, string Html, IReadOnlyList<string> Tags)
{
    /// <summary>True when the title starts with "Azure Update" or "Azure Updates".</summary>
    public bool IsUpdatePost =>
        Title.StartsWith("Azure Update", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A chunk of cleaned post text plus its embedding (filled in later).</summary>
public sealed record Chunk
{
    public required long Id { get; init; }
    public required long PostId { get; init; }
    public required int Ordinal { get; init; }
    public required string Text { get; init; }
    /// <summary>H2/H3 heading that precedes this chunk (empty when unknown).</summary>
    public string SectionTitle { get; init; } = "";
    /// <summary>Azure service name extracted from H2 heading in Update posts; empty for Article posts.</summary>
    public string ServiceName { get; init; } = "";
    /// <summary>"update_item" for structured Update-post bullets; "prose" for all other chunks.</summary>
    public string ChunkType { get; init; } = "prose";
    /// <summary>Small-to-big: neighbouring text (sibling bullets / adjacent chunks)
    /// served to the LLM as generation context. Empty = fall back to Text.
    /// The embedding is always over Text — retrieval keys stay fine-grained.</summary>
    public string ContextText { get; set; } = "";
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
