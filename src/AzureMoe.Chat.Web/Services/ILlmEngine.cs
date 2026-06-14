namespace AzureMoe.Chat.Web.Services;

public sealed record ChatMessage(string Role, string Content);

public interface ILlmEngine
{
    /// <summary>"webgpu", "wasm", or null if not yet loaded.</summary>
    string? Device { get; }
    bool IsLoaded { get; }

    ValueTask LoadAsync(string modelId, string dtype = "q4",
        IProgress<(string Text, int Pct)>? progress = null, CancellationToken ct = default);

    ValueTask ChatAsync(IEnumerable<ChatMessage> messages, Func<string, ValueTask> onToken,
        Action<string>? onCompleted = null, int? maxNewTokens = null, CancellationToken ct = default);

    /// <summary>Run the model and return the full text, without streaming to the UI.
    /// Used for short auxiliary steps (query rewrite, sufficiency evaluation).</summary>
    ValueTask<string> CompleteAsync(IEnumerable<ChatMessage> messages, int maxNewTokens,
        CancellationToken ct = default);

    /// <summary>Signal the worker to abort the current generation (emergency stop).</summary>
    ValueTask InterruptAsync();
}
