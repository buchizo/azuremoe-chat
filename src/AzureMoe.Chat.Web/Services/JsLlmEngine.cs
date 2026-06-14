using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AzureMoe.Chat.Web.Services;

public sealed class JsLlmEngine : ILlmEngine, IAsyncDisposable
{
    private readonly IJSRuntime        _js;
    private readonly NavigationManager _nav;
    private readonly AppConfig         _cfg;
    private IJSObjectReference?                  _module;
    private DotNetObjectReference<JsLlmEngine>?  _loadRef;
    private DotNetObjectReference<JsLlmEngine>?  _chatRef;

    // New token per app load → browser never serves a stale cached worker.
    private static readonly string _workerCacheBust = Guid.NewGuid().ToString("N");

    private IProgress<(string Text, int Pct)>? _loadProgress;
    private Func<string, ValueTask>?            _onToken;
    private bool? _builtinAiAvailable;

    public string? Device   { get; private set; }
    public bool    IsLoaded => Device is not null;

    public JsLlmEngine(IJSRuntime js, NavigationManager nav, AppConfig cfg)
    {
        _js  = js;
        _nav = nav;
        _cfg = cfg;
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync()
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/llm-interop.js");
        return _module;
    }

    /// <summary>
    /// Returns true if Chrome's built-in Prompt API (Gemini Nano) is available.
    /// Result is cached — safe to call multiple times.
    /// </summary>
    public async ValueTask<bool> CheckBuiltinAiAsync(CancellationToken ct = default)
    {
        if (_builtinAiAvailable.HasValue) return _builtinAiAvailable.Value;
        var m = await GetModuleAsync();
        _builtinAiAvailable = await m.InvokeAsync<bool>("checkBuiltinAiAvailability", ct);
        return _builtinAiAvailable.Value;
    }

    public async ValueTask LoadAsync(
        string modelId,
        string dtype = "q4",
        IProgress<(string Text, int Pct)>? progress = null,
        CancellationToken ct = default)
    {
        _loadProgress = progress;
        _loadRef      = DotNetObjectReference.Create(this);

        var m = await GetModuleAsync();

        // Create the Worker before loading the model.
        // The worker is loaded by a plain (non-fingerprinted) URL, which the
        // browser caches aggressively — append a per-load token so a fresh
        // worker is always fetched instead of a stale cached copy. (The worker
        // is tiny, so re-fetching it once per page load is negligible.)
        var workerUrl = $"{_nav.BaseUri.TrimEnd('/')}/js/llm-worker.js?v={_workerCacheBust}";
        await m.InvokeVoidAsync("createLlmWorker", ct, workerUrl);

        var result = await m.InvokeAsync<JsonElement>("loadLlmModel", ct, modelId, dtype, _loadRef);
        Device = result.TryGetProperty("device", out var d) ? d.GetString() : "wasm";
    }

    public async ValueTask ChatAsync(
        IEnumerable<ChatMessage> messages,
        Func<string, ValueTask> onToken,
        Action<string>? onCompleted = null,
        int? maxNewTokens = null,
        CancellationToken ct = default)
    {
        var fullText = await RunAsync(messages, maxNewTokens ?? _cfg.LlmMaxNewTokens, onToken, ct);
        onCompleted?.Invoke(fullText);
    }

    public ValueTask<string> CompleteAsync(
        IEnumerable<ChatMessage> messages, int maxNewTokens, CancellationToken ct = default)
        // No UI streaming — swallow the token deltas and return the full text.
        => RunAsync(messages, maxNewTokens, _ => ValueTask.CompletedTask, ct);

    private async ValueTask<string> RunAsync(
        IEnumerable<ChatMessage> messages, int maxNewTokens,
        Func<string, ValueTask> onToken, CancellationToken ct)
    {
        if (!IsLoaded) throw new InvalidOperationException("LLM not loaded — call LoadAsync first");

        _onToken = onToken;
        _chatRef = DotNetObjectReference.Create(this);

        var messagesJson = JsonSerializer.Serialize(
            messages.Select(m => new { role = m.Role, content = m.Content }));

        try
        {
            var m2 = await GetModuleAsync();
            // Returns { fullText } after all tokens have been streamed.
            var result = await m2.InvokeAsync<JsonElement>(
                "chat", ct, messagesJson, maxNewTokens, _chatRef);
            return result.TryGetProperty("fullText", out var ft) ? ft.GetString() ?? "" : "";
        }
        finally
        {
            _chatRef?.Dispose();
            _chatRef = null;
        }
    }

    /// <summary>Tell the JS worker to abort the in-progress generation.</summary>
    public async ValueTask InterruptAsync()
    {
        if (_module is null) return;
        try { await _module.InvokeVoidAsync("interruptLlm"); } catch { }
    }

    /// <summary>
    /// Terminate the worker and free the loaded model. After this, IsLoaded is
    /// false and the next ChatAsync will reload from scratch. Used by /reload.
    /// </summary>
    public async ValueTask UnloadAsync(CancellationToken ct = default)
    {
        if (_module is not null)
        {
            try { await _module.InvokeVoidAsync("disposeLlmWorker", ct); } catch { }
        }
        _chatRef?.Dispose();
        _chatRef = null;
        Device   = null;   // IsLoaded → false
    }

    [JSInvokable]
    public void OnLlmProgress(string text, int pct) => _loadProgress?.Report((text, pct));

    [JSInvokable]
    public async Task OnToken(string delta)
    {
        if (_onToken is not null) await _onToken(delta);
    }

    public async ValueTask DisposeAsync()
    {
        _loadRef?.Dispose();
        _chatRef?.Dispose();
        if (_module is not null) await _module.DisposeAsync();
    }
}
