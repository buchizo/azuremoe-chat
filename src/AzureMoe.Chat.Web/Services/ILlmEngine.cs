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
        Action<string>? onCompleted = null, CancellationToken ct = default);
}
