using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AzureMoe.Chat.Web.Services;

// ── Shared records (formerly in IGraphStore.cs / RetrievalEngine.cs) ───────

/// <summary>A chunk returned from the graph with its source post metadata.
/// <see cref="Distance"/> is cosine distance (0 = identical). Graph-only hits
/// have Distance 0 and are ranked by the final relevance score.</summary>
public sealed record ChunkResult(
    string Title,
    string Date,
    string Url,
    string Text,
    double Distance,
    long ChunkId = 0);

/// <summary>Per-mode tuning knobs for the retrieval pipeline.</summary>
public sealed record RetrievalOptions(
    int  FinalTopK      = 5,
    int  VectorTopK     = 18,
    bool UseGraph       = true,
    bool IncludeRelated = false,
    int  ExpansionLimit = 10,
    int  DateOverFetch  = 400);

// ── RagInterop ─────────────────────────────────────────────────────────────

/// <summary>
/// Thin C# wrapper around <c>rag-interop.js</c> / <c>rag-worker.js</c>.
/// A single <see cref="RetrieveAsync"/> call replaces the 4-5 separate bridge
/// crossings that the old JsGraphStore + JsEmbedder + RetrievalEngine chain
/// required; the worker handles embed → gather → rank internally.
/// </summary>
public sealed class RagInterop : IAsyncDisposable
{
    private readonly IJSRuntime        _js;
    private readonly NavigationManager _nav;
    private readonly AppConfig         _cfg;
    private IJSObjectReference?        _module;
    private DotNetObjectReference<RagInterop>? _dotnetRef;
    private IProgress<(string Stage, string File, int Pct)>? _progress;
    private bool _initialised;

    public RagInterop(IJSRuntime js, NavigationManager nav, AppConfig cfg)
    {
        _js  = js;
        _nav = nav;
        _cfg = cfg;
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync()
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/rag-interop.js");
        return _module;
    }

    /// <summary>Load the DB bytes and embedding model into the worker.
    /// Reports progress via <paramref name="progress"/> with stage "db" or "embedding".</summary>
    public async ValueTask InitAsync(
        byte[] dbBytes,
        string embeddingModelId,
        IProgress<(string Stage, string File, int Pct)>? progress = null,
        CancellationToken ct = default)
    {
        _progress  = progress;
        _dotnetRef?.Dispose();
        _dotnetRef = DotNetObjectReference.Create(this);

        var m = await GetModuleAsync();
        var workerUrl = _nav.BaseUri.TrimEnd('/') + "/js/rag-worker.js";
        await m.InvokeVoidAsync("createRagWorker", ct, workerUrl);
        await m.InvokeAsync<object>("initRag", ct, dbBytes, embeddingModelId, _dotnetRef);
        _initialised = true;
    }

    /// <summary>Run the full retrieval pipeline in the worker.
    /// Returns ranked <see cref="ChunkResult"/> list ready for context building.</summary>
    public async ValueTask<IReadOnlyList<ChunkResult>> RetrieveAsync(
        string userQuery,
        string searchQuery,
        AnalyzedQuery origQ,
        AnalyzedQuery searchQ,
        RetrievalOptions opt,
        RetrievalMode mode,
        CancellationToken ct = default)
    {
        if (!_initialised) throw new InvalidOperationException("RagInterop not initialised");

        var m = await GetModuleAsync();
        var payload = new
        {
            userQuery,
            searchQuery,
            origQ   = new { date = SerializeDate(origQ.Date),  keywords = origQ.Keywords  },
            searchQ = new { date = SerializeDate(searchQ.Date), keywords = searchQ.Keywords },
            mode    = mode.ToString(),
            config  = new
            {
                vectorTopK     = opt.VectorTopK,
                ragTopK        = opt.FinalTopK,
                useGraph       = opt.UseGraph,
                includeRelated = opt.IncludeRelated,
                expansionLimit = opt.ExpansionLimit,
                dateOverFetch  = opt.DateOverFetch,
                deepMaxRounds  = _cfg.DeepMaxRounds,
            },
        };

        var result = await m.InvokeAsync<JsonElement>("retrieveChunks", ct, payload);
        return ParseChunks(result);
    }

    /// <summary>Terminate the worker so the next <see cref="InitAsync"/> starts fresh.
    /// Used by /reload.</summary>
    public async ValueTask UnloadAsync(CancellationToken ct = default)
    {
        if (_module is not null)
        {
            try { await _module.InvokeVoidAsync("disposeRag", ct); } catch { }
        }
        _initialised = false;
    }

    /// <summary>Called from JS during worker init to forward DB / embedding model progress.</summary>
    [JSInvokable]
    public void OnRagProgress(string stage, string file, int pct) =>
        _progress?.Report((stage, file, pct));

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static object? SerializeDate(DateRange? d) => d is null ? null : new
    {
        fromIso = d.FromIso,
        toIso   = d.ToIso,
        label   = d.Label,
        hard    = d.Hard,
    };

    private static IReadOnlyList<ChunkResult> ParseChunks(JsonElement result)
    {
        var list = new List<ChunkResult>();
        foreach (var row in result.GetProperty("chunks").EnumerateArray())
            list.Add(new ChunkResult(
                Title:    row.TryGetProperty("title",    out var t) ? t.GetString() ?? "" : "",
                Date:     row.TryGetProperty("date",     out var d) ? d.GetString() ?? "" : "",
                Url:      row.TryGetProperty("url",      out var u) ? u.GetString() ?? "" : "",
                Text:     row.TryGetProperty("text",     out var x) ? x.GetString() ?? "" : "",
                Distance: row.TryGetProperty("distance", out var s) && s.ValueKind == JsonValueKind.Number
                              ? s.GetDouble() : 0.0,
                ChunkId:  row.TryGetProperty("cid",      out var i) && i.TryGetInt64(out var id) ? id : 0));
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        _dotnetRef?.Dispose();
        if (_module is not null)
            await _module.DisposeAsync();
    }
}
