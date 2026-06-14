using Microsoft.JSInterop;

namespace AzureMoe.Chat.Web.Services;

public sealed class JsEmbedder : IEmbedder, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;
    private DotNetObjectReference<JsEmbedder>? _dotnetRef;
    private IProgress<(string File, int Pct)>? _progress;
    private bool _loaded;

    public JsEmbedder(IJSRuntime js) => _js = js;

    private async ValueTask<IJSObjectReference> GetModuleAsync()
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/embeddings-interop.js");
        return _module;
    }

    public async ValueTask LoadAsync(
        string modelId,
        IProgress<(string File, int Pct)>? progress = null,
        CancellationToken ct = default)
    {
        _progress   = progress;
        _dotnetRef  = DotNetObjectReference.Create(this);
        var m = await GetModuleAsync();
        await m.InvokeAsync<bool>("loadEmbeddingModel", ct, modelId, _dotnetRef);
        _loaded = true;
    }

    public async ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        if (!_loaded) throw new InvalidOperationException("Embedding model not loaded");
        var m = await GetModuleAsync();
        return await m.InvokeAsync<float[]>("embedQuery", ct, text);
    }

    /// <summary>
    /// Release the loaded embedding pipeline. After this, IsLoaded is false and
    /// the next LoadAsync recreates it. Used by /reload.
    /// </summary>
    public async ValueTask UnloadAsync(CancellationToken ct = default)
    {
        if (_module is not null)
        {
            try { await _module.InvokeVoidAsync("disposeEmbeddingModel", ct); } catch { }
        }
        _loaded = false;
    }

    [JSInvokable]
    public void OnEmbedProgress(string file, int pct) => _progress?.Report((file, pct));

    public async ValueTask DisposeAsync()
    {
        _dotnetRef?.Dispose();
        if (_module is not null) await _module.DisposeAsync();
    }
}
