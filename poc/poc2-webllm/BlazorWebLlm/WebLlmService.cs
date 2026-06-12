using System.Text.Json;
using Microsoft.JSInterop;

namespace Poc2.BlazorWebLlm;

/// <summary>
/// Streams chat completions from WebLLM (running on WebGPU in JS) into C#.
/// Pattern: lazy-import the JS module, pass a DotNetObjectReference so JS can
/// push tokens back via [JSInvokable] callbacks.
/// </summary>
public sealed class WebLlmService(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<WebLlmService>? _selfRef;

    public event Action<string, double>? InitProgress;
    public event Action<string>? TokenReceived;
    public event Action<string>? UsageReceived;
    public event Action<string>? Completed;

    private async Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/webllm-interop.js");

    public async Task<bool> IsWebGpuAvailableAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<bool>("isWebGpuAvailable");
    }

    public async Task InitEngineAsync(string modelId)
    {
        var module = await GetModuleAsync();
        _selfRef ??= DotNetObjectReference.Create(this);
        await module.InvokeAsync<bool>("initEngine", modelId, _selfRef);
    }

    public async Task ChatAsync(IEnumerable<(string Role, string Content)> messages)
    {
        var module = await GetModuleAsync();
        _selfRef ??= DotNetObjectReference.Create(this);
        var json = JsonSerializer.Serialize(messages.Select(m => new { role = m.Role, content = m.Content }));
        await module.InvokeVoidAsync("chat", json, _selfRef);
    }

    public async Task PublishResultAsync(object result)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("publishResult", JsonSerializer.Serialize(result));
    }

    [JSInvokable] public void OnInitProgress(string text, double progress) => InitProgress?.Invoke(text, progress);
    [JSInvokable] public void OnToken(string token) => TokenReceived?.Invoke(token);
    [JSInvokable] public void OnUsage(string usageJson) => UsageReceived?.Invoke(usageJson);
    [JSInvokable] public void OnCompleted(string fullText) => Completed?.Invoke(fullText);

    public async ValueTask DisposeAsync()
    {
        _selfRef?.Dispose();
        if (_module is not null) await _module.DisposeAsync();
    }
}
