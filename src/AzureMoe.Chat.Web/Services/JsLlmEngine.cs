using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AzureMoe.Chat.Web.Services;

public sealed class JsLlmEngine : ILlmEngine, IAsyncDisposable
{
    private readonly IJSRuntime        _js;
    private readonly NavigationManager _nav;
    private IJSObjectReference?                  _module;
    private DotNetObjectReference<JsLlmEngine>?  _loadRef;
    private DotNetObjectReference<JsLlmEngine>?  _chatRef;

    private IProgress<(string Text, int Pct)>? _loadProgress;
    private Func<string, ValueTask>?            _onToken;
    private bool? _builtinAiAvailable;

    public string? Device   { get; private set; }
    public bool    IsLoaded => Device is not null;

    public JsLlmEngine(IJSRuntime js, NavigationManager nav)
    {
        _js  = js;
        _nav = nav;
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
        var workerUrl = _nav.BaseUri.TrimEnd('/') + "/js/llm-worker.js";
        await m.InvokeVoidAsync("createLlmWorker", ct, workerUrl);

        var result = await m.InvokeAsync<JsonElement>("loadLlmModel", ct, modelId, dtype, _loadRef);
        Device = result.TryGetProperty("device", out var d) ? d.GetString() : "wasm";
    }

    public async ValueTask ChatAsync(
        IEnumerable<ChatMessage> messages,
        Func<string, ValueTask> onToken,
        Action<string>? onCompleted = null,
        CancellationToken ct = default)
    {
        if (!IsLoaded) throw new InvalidOperationException("LLM not loaded — call LoadAsync first");

        _onToken = onToken;
        _chatRef = DotNetObjectReference.Create(this);

        var messagesJson = JsonSerializer.Serialize(
            messages.Select(m => new { role = m.Role, content = m.Content }));

        var m2 = await GetModuleAsync();
        // Returns { fullText } after all tokens have been streamed.
        var result = await m2.InvokeAsync<JsonElement>("chat", ct, messagesJson, 1024, _chatRef);

        _chatRef?.Dispose();
        _chatRef = null;

        var fullText = result.TryGetProperty("fullText", out var ft) ? ft.GetString() ?? "" : "";
        onCompleted?.Invoke(fullText);
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
