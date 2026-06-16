namespace AzureMoe.Chat.Web.Services;

/// <summary>
/// Runtime proxy that routes ILlmEngine calls to either the local WASM engine
/// (JsLlmEngine) or an HTTP OpenAI-compatible endpoint (HttpLlmEngine).
/// The /llm command in Home.razor calls UseHttp / UseLocal to switch.
/// </summary>
public sealed class LlmEngineRouter : ILlmEngine
{
    private readonly JsLlmEngine   _js;
    private readonly HttpLlmEngine _http;
    private ILlmEngine _current;

    public LlmEngineRouter(JsLlmEngine js, HttpLlmEngine http)
    {
        _js      = js;
        _http    = http;
        _current = js;
    }

    // ── Mode switching ──────────────────────────────────────────────────────

    public bool    IsHttpMode   => _current == _http;
    public string? HttpEndpoint => _http.Endpoint;
    public string? HttpModel    => _http.Model;

    public void UseHttp(string endpoint, string? model)
    {
        _http.Configure(endpoint, model);
        _current = _http;
    }

    public void UseLocal()
    {
        _http.Unconfigure();
        _current = _js;
    }

    // ── ILlmEngine: delegated to _current ──────────────────────────────────

    public string? Device   => _current.Device;
    public bool    IsLoaded => _current.IsLoaded;

    // LoadAsync / UnloadAsync always target the JS engine so startup still works
    // regardless of which mode is active.
    public ValueTask LoadAsync(string modelId, string dtype = "q4",
        IProgress<(string Text, int Pct)>? progress = null, CancellationToken ct = default)
        => _js.LoadAsync(modelId, dtype, progress, ct);

    public ValueTask UnloadAsync(CancellationToken ct = default)
        => _js.UnloadAsync(ct);

    public ValueTask ChatAsync(IEnumerable<ChatMessage> messages, Func<string, ValueTask> onToken,
        Action<string>? onCompleted = null, int? maxNewTokens = null, CancellationToken ct = default)
        => _current.ChatAsync(messages, onToken, onCompleted, maxNewTokens, ct);

    public ValueTask<string> CompleteAsync(IEnumerable<ChatMessage> messages, int maxNewTokens,
        CancellationToken ct = default)
        => _current.CompleteAsync(messages, maxNewTokens, ct);

    public ValueTask InterruptAsync() => _current.InterruptAsync();
}
