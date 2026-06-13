using System.Text;

namespace AzureMoe.Chat.Web.Services;

public sealed class RagPipeline
{
    private readonly IGraphStore _graph;
    private readonly IEmbedder   _embedder;
    private readonly ILlmEngine  _llm;
    private readonly AppConfig   _cfg;

    public RagPipeline(IGraphStore graph, IEmbedder embedder, ILlmEngine llm, AppConfig cfg)
    {
        _graph    = graph;
        _embedder = embedder;
        _llm      = llm;
        _cfg      = cfg;
    }

    // Run a full RAG turn. Returns cited sources.
    public async ValueTask<IReadOnlyList<ChunkResult>> RunAsync(
        string userQuery,
        IReadOnlyList<ChatMessage> history,
        Func<string, ValueTask> onToken,
        Action<string>? onCompleted = null,
        CancellationToken ct = default)
    {
        // 1. Embed query.
        var queryVec = await _embedder.EmbedQueryAsync(userQuery, ct);

        // 2. Vector search.
        var chunks = await _graph.VectorSearchAsync(queryVec, _cfg.RagTopK, ct);

        // 3. Build context — trim to MaxContextChars.
        var context = BuildContext(chunks);

        // 4. Build messages.
        var systemContent = _cfg.SystemPrompt + "\n\n## 参考情報\n\n" + context;
        var messages = new List<ChatMessage>
        {
            new("system", systemContent),
        };
        foreach (var h in history)
            messages.Add(h);
        messages.Add(new("user", userQuery));

        // 5. Stream LLM response.
        await _llm.ChatAsync(messages, onToken, onCompleted, ct);

        return chunks;
    }

    private string BuildContext(IReadOnlyList<ChunkResult> chunks)
    {
        var sb   = new StringBuilder();
        var used = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            var c   = chunks[i];
            var sim = (1.0 - c.Distance).ToString("F3");
            var date = c.Date.Length >= 10 ? c.Date[..10] : c.Date;
            var header = $"[{i + 1}] {c.Title} ({date}) — 類似度 {sim}\n{c.Url}\n\n";
            var body = c.Text + "\n\n";

            if (used + header.Length + body.Length > _cfg.MaxContextChars && used > 0)
                break;

            sb.Append(header);
            sb.Append(body);
            used += header.Length + body.Length;
        }

        return sb.ToString().TrimEnd();
    }
}
