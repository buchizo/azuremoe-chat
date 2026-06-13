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

    public async ValueTask<IReadOnlyList<ChunkResult>> VectorSearchAsync(
        float[] queryVec, int topK, CancellationToken ct = default)
    {
        if (!_initialised) throw new InvalidOperationException("GraphStore not initialised");

        var vals = string.Join(",", queryVec.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        var cypher = $"""
            CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_emb_idx', CAST([{vals}] AS FLOAT[{queryVec.Length}]), {topK})
            YIELD node AS c, distance
            MATCH (p:Post)-[:HAS_CHUNK]->(c)
            RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, distance
            ORDER BY distance
            """;

        var m = await GetModuleAsync();
        var res = await m.InvokeAsync<JsonElement>("query", ct, cypher);

        var rows = res.GetProperty("rows");
        var results = new List<ChunkResult>(topK);
        foreach (var row in rows.EnumerateArray())
        {
            results.Add(new ChunkResult(
                Title:    row.TryGetProperty("title",    out var t) ? t.GetString() ?? "" : "",
                Date:     row.TryGetProperty("date",     out var d) ? d.GetString() ?? "" : "",
                Url:      row.TryGetProperty("url",      out var u) ? u.GetString() ?? "" : "",
                Text:     row.TryGetProperty("text",     out var x) ? x.GetString() ?? "" : "",
                Distance: row.TryGetProperty("distance", out var s) ? s.GetDouble() : 1.0));
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
            await _module.DisposeAsync();
    }
}
