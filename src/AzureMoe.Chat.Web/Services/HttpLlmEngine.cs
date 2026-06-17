using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureMoe.Chat.Web.Services;

/// <summary>
/// ILlmEngine implementation that proxies to an OpenAI-compatible HTTP endpoint.
/// Supports SSE streaming. Used by LlmEngineRouter when the user sets /llm {endpoint}.
/// </summary>
public sealed class HttpLlmEngine : ILlmEngine
{
    private readonly HttpClient _http;
    private CancellationTokenSource? _genCts;

    public string? Endpoint { get; private set; }
    public string? Model    { get; private set; }

    public string? Device      => Endpoint is not null ? "http" : null;
    public string? LoadedDtype => null;
    public bool    IsLoaded    => Endpoint is not null;

    public HttpLlmEngine(HttpClient http) => _http = http;

    public void Configure(string endpoint, string? model)
    {
        Endpoint = endpoint.TrimEnd('/');
        Model    = model;
    }

    public void Unconfigure()
    {
        Endpoint = null;
        Model    = null;
    }

    // ── ILlmEngine ─────────────────────────────────────────────────────────

    public ValueTask LoadAsync(string modelId, string dtype = "q4",
        IProgress<(string Text, int Pct)>? progress = null, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask UnloadAsync(CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public async ValueTask InterruptAsync()
    {
        if (_genCts is not null)
            await _genCts.CancelAsync();
    }

    public async ValueTask ChatAsync(
        IEnumerable<ChatMessage> messages,
        Func<string, ValueTask> onToken,
        Action<string>? onCompleted = null,
        int? maxNewTokens = null,
        CancellationToken ct = default)
    {
        var full = await StreamCompletionAsync(messages.ToList(), maxNewTokens, onToken, ct);
        onCompleted?.Invoke(full);
    }

    public ValueTask<string> CompleteAsync(
        IEnumerable<ChatMessage> messages, int maxNewTokens, CancellationToken ct = default)
        => StreamCompletionAsync(messages.ToList(), maxNewTokens, _ => ValueTask.CompletedTask, ct);

    // ── Private helpers ─────────────────────────────────────────────────────

    private async ValueTask<string> StreamCompletionAsync(
        List<ChatMessage> messages,
        int? maxNewTokens,
        Func<string, ValueTask> onToken,
        CancellationToken ct)
    {
        if (Endpoint is null) throw new InvalidOperationException("HttpLlmEngine not configured");

        _genCts?.Dispose();
        _genCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linked = _genCts.Token;

        var body = BuildRequestBody(messages, maxNewTokens);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}/chat/completions");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked);
        }
        catch (Exception ex) when (!linked.IsCancellationRequested)
        {
            // http:// endpoint from an https:// page → Mixed Content block (browser drops the
            // request before it even reaches the server; CORS settings are irrelevant).
            // https:// endpoint → likely a CORS preflight failure.
            var hint = Endpoint?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
                ? "HTTPS のページから http:// エンドポイントへの接続はブラウザの Mixed Content ポリシーによりブロックされます。" +
                  "ローカルで使うには http:// でアプリを開いてください（開発サーバー: dotnet run → http://localhost:5001）。"
                : "CORS が有効になっているか確認してください（LM Studio: サーバー設定 → Allow CORS / Ollama: OLLAMA_ORIGINS 環境変数）。";
            throw new InvalidOperationException(
                $"LLM エンドポイント ({Endpoint}) への接続に失敗しました。{hint} 詳細: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(linked);
                throw new InvalidOperationException(
                    $"HTTP {(int)response.StatusCode}: {err.Trim()}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(linked);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            var sb = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync(linked)) is not null)
            {
                linked.ThrowIfCancellationRequested();
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line[6..];
                if (data == "[DONE]") break;

                string? delta = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var d) &&
                        d.TryGetProperty("content", out var c))
                    {
                        delta = c.GetString();
                    }
                }
                catch { /* malformed SSE chunk — skip */ }

                if (!string.IsNullOrEmpty(delta))
                {
                    sb.Append(delta);
                    await onToken(delta);
                }
            }

            return sb.ToString();
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string BuildRequestBody(List<ChatMessage> messages, int? maxNewTokens)
    {
        var payload = new RequestPayload
        {
            Model      = Model,
            Stream     = true,
            MaxTokens  = maxNewTokens,
            Messages   = messages.Select(m => new MessageDto(m.Role, m.Content)).ToArray(),
        };
        return JsonSerializer.Serialize(payload, _jsonOpts);
    }

    // ── DTO records ─────────────────────────────────────────────────────────

    private sealed record RequestPayload
    {
        [JsonPropertyName("model")]      public string?       Model     { get; init; }
        [JsonPropertyName("stream")]     public bool          Stream    { get; init; }
        [JsonPropertyName("max_tokens")] public int?          MaxTokens { get; init; }
        [JsonPropertyName("messages")]   public MessageDto[]  Messages  { get; init; } = [];
    }

    private sealed record MessageDto(
        [property: JsonPropertyName("role")]    string Role,
        [property: JsonPropertyName("content")] string Content);
}
