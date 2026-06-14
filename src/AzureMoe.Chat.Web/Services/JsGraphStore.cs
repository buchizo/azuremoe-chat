using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AzureMoe.Chat.Web.Services;

public sealed class JsGraphStore : IGraphStore, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly NavigationManager _nav;
    private IJSObjectReference? _module;
    private bool _initialised;

    public JsGraphStore(IJSRuntime js, NavigationManager nav)
    {
        _js  = js;
        _nav = nav;
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync()
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/ladybug-interop.js");
        return _module;
    }

    public async ValueTask InitAsync(byte[] dbBytes, CancellationToken ct = default)
    {
        var m = await GetModuleAsync();

        // Register the worker URL with the main-thread interop module.
        var workerUrl = _nav.BaseUri.TrimEnd('/') + "/js/ladybug-worker.js";
        await m.InvokeVoidAsync("createWorker", ct, workerUrl);

        // Send DB bytes to the worker (byte[] marshals as Uint8Array in JS).
        await m.InvokeAsync<object>("initDb", ct, dbBytes);
        _initialised = true;
    }

    // ── Vector search ────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<ChunkResult>> VectorSearchAsync(
        float[] queryVec, int topK, CancellationToken ct = default)
    {
        var cypher = $"""
            CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_emb_idx', {VecLiteral(queryVec)}, {topK})
            YIELD node AS c, distance
            MATCH (p:Post)-[:HAS_CHUNK]->(c)
            RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, distance
            ORDER BY distance
            """;
        return QueryChunksAsync(cypher, ct);
    }

    public ValueTask<IReadOnlyList<ChunkResult>> VectorSearchInDateRangeAsync(
        float[] queryVec, string fromIso, string toIso, int topK, int overFetch,
        CancellationToken ct = default)
    {
        var cypher = $"""
            CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_emb_idx', {VecLiteral(queryVec)}, {overFetch})
            YIELD node AS c, distance
            MATCH (p:Post)-[:HAS_CHUNK]->(c)
            WHERE p.date >= '{Esc(fromIso)}' AND p.date < '{Esc(toIso)}'
            RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, distance
            ORDER BY distance
            LIMIT {topK}
            """;
        return QueryChunksAsync(cypher, ct);
    }

    public ValueTask<IReadOnlyList<ChunkResult>> ChunksByDateRangeAsync(
        string fromIso, string toIso, int topK, CancellationToken ct = default)
    {
        var cypher = $"""
            MATCH (p:Post)-[:HAS_CHUNK]->(c:Chunk)
            WHERE p.date >= '{Esc(fromIso)}' AND p.date < '{Esc(toIso)}'
            RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, 0.0 AS distance
            ORDER BY p.date DESC, c.ordinal ASC
            LIMIT {topK}
            """;
        return QueryChunksAsync(cypher, ct);
    }

    // ── Graph expansion ───────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<GraphChunk>> ExpandByTagsAsync(
        IReadOnlyList<long> seedChunkIds, int limit, CancellationToken ct = default)
    {
        if (seedChunkIds.Count == 0) return Empty();
        var ids = IdList(seedChunkIds);
        var cypher = $"""
            MATCH (sp:Post)-[:HAS_CHUNK]->(seed:Chunk) WHERE seed.id IN {ids}
            MATCH (sp)-[:TAGGED]->(t:Tag)<-[:TAGGED]-(p2:Post)-[:HAS_CHUNK]->(c2:Chunk)
            WHERE NOT c2.id IN {ids}
            RETURN p2.title AS title, p2.date AS date, p2.url AS url, c2.text AS text,
                   c2.id AS cid, count(DISTINCT t) AS shared
            ORDER BY shared DESC
            LIMIT {limit}
            """;
        return QueryGraphChunksAsync(cypher, ct);
    }

    public ValueTask<IReadOnlyList<GraphChunk>> ExpandByEntitiesAsync(
        IReadOnlyList<long> seedChunkIds, int limit, bool includeRelated, CancellationToken ct = default)
    {
        if (seedChunkIds.Count == 0) return Empty();
        var ids = IdList(seedChunkIds);
        var hop  = includeRelated ? "-[:RELATED_TO*0..1]-(e2:Entity)" : "";
        var tail = includeRelated ? "e2" : "e";
        var cypher = $"""
            MATCH (seed:Chunk)-[:MENTIONS]->(e:Entity) WHERE seed.id IN {ids}
            MATCH (e){hop}<-[:MENTIONS]-(c2:Chunk) WHERE NOT c2.id IN {ids}
            MATCH (p:Post)-[:HAS_CHUNK]->(c2)
            RETURN p.title AS title, p.date AS date, p.url AS url, c2.text AS text,
                   c2.id AS cid, count(DISTINCT {tail}) AS shared
            ORDER BY shared DESC
            LIMIT {limit}
            """;
        return QueryGraphChunksAsync(cypher, ct);
    }

    public ValueTask<IReadOnlyList<GraphChunk>> ExpandByServiceAsync(
        IReadOnlyList<long> seedChunkIds, int limit, CancellationToken ct = default)
    {
        if (seedChunkIds.Count == 0) return Empty();
        var ids = IdList(seedChunkIds);
        var cypher = $"""
            MATCH (sp:Post)-[:HAS_CHUNK]->(seed:Chunk) WHERE seed.id IN {ids}
            MATCH (sp)-[:COVERS_SERVICE]->(s:AzureService)<-[:COVERS_SERVICE]-(p2:Post)-[:HAS_CHUNK]->(c2:Chunk)
            WHERE NOT c2.id IN {ids}
            RETURN p2.title AS title, p2.date AS date, p2.url AS url, c2.text AS text,
                   c2.id AS cid, count(DISTINCT s) AS shared
            ORDER BY shared DESC
            LIMIT {limit}
            """;
        return QueryGraphChunksAsync(cypher, ct);
    }

    public ValueTask<IReadOnlyList<GraphChunk>> SearchByKeywordsAsync(
        IReadOnlyList<string> keywords, int limit, CancellationToken ct = default)
    {
        var kws = keywords.Where(k => !string.IsNullOrWhiteSpace(k))
                          .Select(k => k.Trim().ToLowerInvariant())
                          .Where(k => k.Length >= 2)
                          .Distinct()
                          .Take(6)
                          .ToList();
        if (kws.Count == 0) return Empty();

        // Match entity / service / tag names against the keywords, then pull the
        // chunks that reference them. UNION keeps each leg simple and indexable.
        var entPred = string.Join(" OR ", kws.Select(k => $"toLower(e.name) CONTAINS '{Esc(k)}'"));
        var svcPred = string.Join(" OR ", kws.Select(k => $"toLower(s.name) CONTAINS '{Esc(k)}'"));
        var tagPred = string.Join(" OR ", kws.Select(k => $"toLower(t.name) CONTAINS '{Esc(k)}'"));
        var cypher = $"""
            MATCH (e:Entity)<-[:MENTIONS]-(c:Chunk) WHERE {entPred}
            MATCH (p:Post)-[:HAS_CHUNK]->(c)
            RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, 1 AS shared
            UNION
            MATCH (s:AzureService)<-[:COVERS_SERVICE]-(p:Post)-[:HAS_CHUNK]->(c:Chunk) WHERE {svcPred}
            RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, 1 AS shared
            UNION
            MATCH (t:Tag)<-[:TAGGED]-(p:Post)-[:HAS_CHUNK]->(c:Chunk) WHERE {tagPred}
            RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, c.id AS cid, 1 AS shared
            LIMIT {limit}
            """;
        return QueryGraphChunksAsync(cypher, ct);
    }

    // ── Query execution / parsing ──────────────────────────────────────────────

    private async ValueTask<IReadOnlyList<ChunkResult>> QueryChunksAsync(string cypher, CancellationToken ct)
    {
        var rows = await RunAsync(cypher, ct);
        var results = new List<ChunkResult>();
        foreach (var row in rows)
            results.Add(ReadChunk(row));
        return results;
    }

    private async ValueTask<IReadOnlyList<GraphChunk>> QueryGraphChunksAsync(string cypher, CancellationToken ct)
    {
        var rows = await RunAsync(cypher, ct);
        var results = new List<GraphChunk>();
        foreach (var row in rows)
        {
            var shared = row.TryGetProperty("shared", out var s) && s.TryGetInt32(out var n) ? n : 1;
            results.Add(new GraphChunk(ReadChunk(row), shared));
        }
        return results;
    }

    private async ValueTask<JsonElement.ArrayEnumerator> RunAsync(string cypher, CancellationToken ct)
    {
        if (!_initialised) throw new InvalidOperationException("GraphStore not initialised");
        var m = await GetModuleAsync();
        var res = await m.InvokeAsync<JsonElement>("query", ct, cypher);
        return res.GetProperty("rows").EnumerateArray();
    }

    private static ChunkResult ReadChunk(JsonElement row) => new(
        Title:    row.TryGetProperty("title",    out var t) ? t.GetString() ?? "" : "",
        Date:     row.TryGetProperty("date",     out var d) ? d.GetString() ?? "" : "",
        Url:      row.TryGetProperty("url",      out var u) ? u.GetString() ?? "" : "",
        Text:     row.TryGetProperty("text",     out var x) ? x.GetString() ?? "" : "",
        Distance: row.TryGetProperty("distance", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0.0,
        ChunkId:  row.TryGetProperty("cid",      out var i) && i.TryGetInt64(out var id) ? id : 0);

    private static ValueTask<IReadOnlyList<GraphChunk>> Empty() =>
        ValueTask.FromResult<IReadOnlyList<GraphChunk>>([]);

    private static string VecLiteral(float[] vec)
    {
        var vals = string.Join(",", vec.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        return $"CAST([{vals}] AS FLOAT[{vec.Length}])";
    }

    private static string IdList(IReadOnlyList<long> ids)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(ids[i].ToString(CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
            await _module.DisposeAsync();
    }
}
