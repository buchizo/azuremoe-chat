using System.Text;
using System.Text.RegularExpressions;
using AzureMoe.Chat.Web;

namespace AzureMoe.Chat.Web.Services;

/// <summary>
/// Orchestrates a RAG turn. Retrieval is graph- and date-aware (see
/// <see cref="RetrievalEngine"/>); the depth of the turn is set by
/// <see cref="AppConfig.RetrievalMode"/>:
///   Fast   — pure vector search, no graph/rewrite. 1 LLM call. Shallow & quick.
///   Normal — history-aware query rewrite + graph expansion. ~2 LLM calls.
///   Deep   — wider graph (related-entity hop) + iterative retrieve→evaluate→
///            re-retrieve. Many LLM calls. Slow & thorough.
/// Progress is reported through <c>onStatus</c> so the UI can show what each
/// step is doing (and the modes visibly differ). The retrieved context is
/// attached to the final user turn (not the system prompt) and history is
/// trimmed, so the current question stays salient.
/// </summary>
public sealed class RagPipeline
{
    private readonly RetrievalEngine _retrieval;
    private readonly QueryAnalyzer   _analyzer;
    private readonly IEmbedder       _embedder;
    private readonly ILlmEngine      _llm;
    private readonly AppConfig       _cfg;

    public RagPipeline(RetrievalEngine retrieval, QueryAnalyzer analyzer,
        IEmbedder embedder, ILlmEngine llm, AppConfig cfg)
    {
        _retrieval = retrieval;
        _analyzer  = analyzer;
        _embedder  = embedder;
        _llm       = llm;
        _cfg       = cfg;
    }

    public async ValueTask<IReadOnlyList<ChunkResult>> RunAsync(
        string userQuery,
        IReadOnlyList<ChatMessage> history,
        Func<string, ValueTask> onToken,
        Action<string>? onCompleted = null,
        Action<string>? onStatus = null,
        Action<string>? onWarning = null,
        Action? onStreamReset = null,
        CancellationToken ct = default)
    {
        void Status(string s) => onStatus?.Invoke(s);

        var trimmed = TrimHistory(history);

        // The original question is the authority for final relevance ranking — every
        // mode collects candidates differently but ranks them against this vector.
        var origVec = await _embedder.EmbedQueryAsync(userQuery, ct);

        var sources = _cfg.RetrievalMode switch
        {
            RetrievalMode.Deep => await RetrieveDeepAsync(userQuery, trimmed, origVec, Status, ct),
            RetrievalMode.Fast => await RetrieveOnceAsync(userQuery, rewrite: false, trimmed, origVec, Status, ct),
            _                  => await RetrieveOnceAsync(userQuery, rewrite: true,  trimmed, origVec, Status, ct),
        };

        // Merge chunks from the same blog post (same URL) into one numbered
        // reference, so both the context the model cites and the sources shown
        // to the user list each article once.
        var references = GroupByPost(sources);
        var context    = BuildContext(references);

        // No grounding material → don't let the model answer from its own
        // knowledge. Return a canned "not found" instead of risking a hallucination.
        if (string.IsNullOrWhiteSpace(context))
        {
            onCompleted?.Invoke(NoContextMessage);
            return references;
        }

        // Streamed generation. The stream is a live PREVIEW only — we commit the
        // final answer (onCompleted) ourselves after the grounding check, so a
        // failed attempt can be regenerated without showing two answers.
        async ValueTask<string> GenerateAsync(List<ChatMessage> msgs)
        {
            onStreamReset?.Invoke();           // clear any previous preview
            var text = "";
            await _llm.ChatAsync(msgs, onToken, full => text = full, _cfg.LlmMaxNewTokens, ct);
            return SanitizeAnswer(text);
        }

        // If the user's question carries a date window, pass its label so the
        // prompt can pin the answer's dates (small models otherwise drift to
        // years from their training data). Only for an explicit window (年/月);
        // a vague "最近" must not turn into "年月を『最近』に統一".
        var qDate     = _analyzer.Analyze(userQuery).Date;
        var dateLabel = qDate is { Hard: true } ? qDate.Label : null;

        Status("あずもが回答を考えています…");
        var answer   = await GenerateAsync(BuildMessages(trimmed, userQuery, context, dateLabel, strict: false));
        var grounded = true;

        // Grounding check (all modes) so the model can't quietly fall back to
        // general knowledge. On failure, regenerate once with a stricter prompt.
        if (_cfg.VerifyGrounding && !string.IsNullOrWhiteSpace(answer))
        {
            Status("回答が参考情報に基づいているか確認しています…");
            grounded = await IsGroundedAsync(answer, context, ct);

            if (!grounded)
            {
                Status("根拠に厳密に基づいて回答し直しています…");
                var retry = await GenerateAsync(BuildMessages(trimmed, userQuery, context, dateLabel, strict: true));
                if (!string.IsNullOrWhiteSpace(retry))
                {
                    answer   = retry;
                    grounded = await IsGroundedAsync(retry, context, ct);
                }
            }
        }

        // Commit the final answer once, then warn if it still isn't grounded.
        onCompleted?.Invoke(answer);
        if (_cfg.VerifyGrounding && !grounded)
            onWarning?.Invoke(
                "⚠ この回答には、参考情報だけでは確認できない内容が含まれている可能性があります。" +
                "下の参考記事で裏付けを確認してね。");

        return references;
    }

    private const string NoContextMessage =
        "参考情報（ブログ記事）が見つからなかったよ、先輩。質問の言い方や時期を変えて、もう一度試してみてね。";

    // Markers where a small model tends to run off the rails: continuing into a
    // new conversation turn (role tags), template/special-token leakage, or HTML
    // comments. We cut the answer at the first such marker.
    private static readonly string[] StopMarkers =
    [
        "<Message", "</Message", "<|", "<!--", "```html",
        "\nUser:", "\nuser:", "\nHuman:", "\nassistant", "\nアシスタント", "\nユーザー",
    ];

    // Boilerplate a small model adds despite the prompt: a leading greeting, and a
    // trailing social-pleasantry sentence ("…他にお手伝いできることはありますか？").
    private static readonly Regex LeadingGreeting = new(
        @"^\s*(?:こんにちは|こんばんは|おはようございます|おはよう|やあ|どうも)[、。!！~～\s]*",
        RegexOptions.Compiled);
    private static readonly Regex TrailingPleasantry = new(
        @"\s*[^。．！？\n]*(?:お手伝いできること|お手伝いできれば|お役に立て|お気軽に|気軽に聞|気軽にお|ご不明な点|ご質問|お知らせください|遠慮なく|聞いてください|聞いてね)[^。．！？\n]*[。．！？]?\s*$",
        RegexOptions.Compiled);

    /// <summary>Trim runaway artifacts (a hallucinated second turn, leaked role
    /// tags, HTML comments) and conversational boilerplate (greeting / closing
    /// pleasantry) so the displayed answer stays a single clean response.</summary>
    private static string SanitizeAnswer(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var cut = text.Length;
        foreach (var m in StopMarkers)
        {
            var i = text.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && i < cut) cut = i;
        }
        var s = text[..cut].TrimEnd();
        s = LeadingGreeting.Replace(s, "");
        s = TrailingPleasantry.Replace(s, "");
        return s.Trim();
    }

    /// <summary>Second LLM pass: is the answer supported only by the context?
    /// Deliberately lenient on ambiguity (don't cry wolf) but flags clear drift
    /// into facts/names/dates absent from the context.</summary>
    private async ValueTask<bool> IsGroundedAsync(string answer, string context, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new("system",
                "あなたは厳密な校正者です。『回答』が『参考情報』だけで裏付けられるか判定してください。" +
                "参考情報に書かれていない事実・製品名・数値・日付が回答に含まれる場合は不合格です。" +
                "裏付けられるなら『OK』、そうでなければ『NG』とだけ出力してください。"),
            new("user", $"## 参考情報\n{context}\n\n## 回答\n{answer}\n\n判定（OK か NG のみ）:"),
        };

        var raw = (await _llm.CompleteAsync(messages, 8, ct)).ToUpperInvariant();
        // Treat only an explicit NG (without a competing OK) as ungrounded.
        return !(raw.Contains("NG") && !raw.Contains("OK"));
    }

    // ── Fast / Normal: one recall pass, then rank against the original question ─

    private async ValueTask<IReadOnlyList<ChunkResult>> RetrieveOnceAsync(
        string userQuery, bool rewrite, IReadOnlyList<ChatMessage> history,
        float[] origVec, Action<string> status, CancellationToken ct)
    {
        var searchQuery = userQuery;
        var searchVec   = origVec;
        if (rewrite && history.Count > 0)
        {
            status("質問の意図を整理しています…");
            searchQuery = await RewriteQueryAsync(userQuery, history, ct);
            if (searchQuery != userQuery)
                searchVec = await _embedder.EmbedQueryAsync(searchQuery, ct);
        }

        status("関連する記事を検索しています…");
        var opt  = OptionsFor(_cfg.RetrievalMode);
        var cand = await _retrieval.GatherAsync(searchVec, _analyzer.Analyze(searchQuery), opt, ct);
        return await _retrieval.RankAndSelectAsync(cand, origVec, _analyzer.Analyze(userQuery), opt, ct);
    }

    // ── Deep: multi-query recall (deterministic, on-topic) → rank the union ─────

    private async ValueTask<IReadOnlyList<ChunkResult>> RetrieveDeepAsync(
        string userQuery, IReadOnlyList<ChatMessage> history,
        float[] origVec, Action<string> status, CancellationToken ct)
    {
        var opt = OptionsFor(RetrievalMode.Deep);

        var searchQuery = userQuery;
        var searchVec   = origVec;
        if (history.Count > 0)
        {
            status("質問の意図を整理しています…");
            searchQuery = await RewriteQueryAsync(userQuery, history, ct);
            if (searchQuery != userQuery)
                searchVec = await _embedder.EmbedQueryAsync(searchQuery, ct);
        }

        var union = new Dictionary<long, Candidate>();
        void Merge(IReadOnlyList<Candidate> cs)
        {
            foreach (var c in cs) union.TryAdd(c.Chunk.ChunkId, c);
        }

        status("関連する記事を検索しています… (1回目)");
        var round0 = await _retrieval.GatherAsync(searchVec, _analyzer.Analyze(searchQuery), opt, ct);
        Merge(round0);

        // Deterministic, on-topic follow-ups: search by the titles of the best
        // round-0 articles. Stays grounded in the corpus (no weak-model query
        // planning), and the final rank against the original question filters
        // anything tangential these bring in.
        var followups = round0.Select(c => c.Chunk.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .Take(Math.Max(0, _cfg.DeepMaxRounds - 1))
            .ToList();

        for (var i = 0; i < followups.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            status($"さらに詳しく検索しています… ({i + 2}回目)");
            var fv = await _embedder.EmbedQueryAsync(followups[i], ct);
            Merge(await _retrieval.GatherAsync(fv, _analyzer.Analyze(followups[i]), opt, ct));
        }

        return await _retrieval.RankAndSelectAsync(
            union.Values.ToList(), origVec, _analyzer.Analyze(userQuery), opt, ct);
    }

    // ── Query rewrite (history-aware) ──────────────────────────────────────────

    private async ValueTask<string> RewriteQueryAsync(
        string userQuery, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new("system",
                "あなたは検索クエリ書き換えアシスタントです。会話の文脈を踏まえ、最後のユーザーの質問を、" +
                "それ単体で検索できる完結した日本語の検索クエリに書き換えてください。" +
                "検索クエリの一行だけを出力し、説明や記号・引用符は付けないでください。"),
        };
        messages.AddRange(history);
        messages.Add(new ChatMessage("user", userQuery));

        var raw     = await _llm.CompleteAsync(messages, _cfg.LlmRewriteMaxTokens, ct);
        var cleaned = CleanLine(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
        return cleaned.Length is >= 2 and <= 200 ? cleaned : userQuery;
    }

    private static string CleanLine(string s) =>
        s.Trim().Trim('"', '\'', '「', '」', '`', '・', '-', '*', ' ').Trim();

    // ── Prompt assembly ────────────────────────────────────────────────────────

    private RetrievalOptions OptionsFor(RetrievalMode mode) => mode switch
    {
        // Fast: pure vector, no graph traversal, fewest sources → quickest.
        RetrievalMode.Fast => new RetrievalOptions(
            FinalTopK: Math.Max(3, _cfg.RagTopK - 2), VectorTopK: 12, UseGraph: false,
            IncludeRelated: false, ExpansionLimit: 0, DateOverFetch: 300),
        // Deep: wide recall + related-entity hop. Precision comes from the final
        // rank against the original question, so recall can be generous.
        RetrievalMode.Deep => new RetrievalOptions(
            FinalTopK: _cfg.RagTopK, VectorTopK: 30, UseGraph: true,
            IncludeRelated: true, ExpansionLimit: 14, DateOverFetch: 600),
        // Normal: balanced graph expansion.
        _ => new RetrievalOptions(
            FinalTopK: Math.Max(4, _cfg.RagTopK - 1), VectorTopK: 18, UseGraph: true,
            IncludeRelated: false, ExpansionLimit: 10, DateOverFetch: 400),
    };

    private IReadOnlyList<ChatMessage> TrimHistory(IReadOnlyList<ChatMessage> history)
    {
        var keep = _cfg.HistoryTurns * 2;
        return history.Count <= keep ? history : history.Skip(history.Count - keep).ToList();
    }

    private List<ChatMessage> BuildMessages(
        IReadOnlyList<ChatMessage> history, string userQuery, string context, string? dateLabel,
        bool strict)
    {
        var messages = new List<ChatMessage> { new("system", _cfg.SystemPrompt) };
        messages.AddRange(history);

        // Context is attached to the latest user turn so it (and the question)
        // are the most recent thing the model sees — not buried in the system prompt.
        string userContent;
        if (string.IsNullOrWhiteSpace(context))
        {
            userContent = userQuery;
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append("## 参考情報\n\n").Append(context).Append("\n\n");
            // Stricter retry (the first answer wasn't grounded): demand
            // citation-backed, context-only output up front.
            if (strict)
                sb.Append("## 重要\n前回の回答は参考情報で裏付けられていませんでした。今回は参考情報に明記された内容だけを、")
                  .Append("該当する記事番号 [1] [2] を本文中に引用しながら答えてください。")
                  .Append("参考情報に書かれていないことは一切書かず、該当情報が無ければ「参考情報には載っていなかったよ、先輩」とだけ答えること。\n\n");
            if (!string.IsNullOrEmpty(dateLabel))
                sb.Append($"## 重要な制約\nユーザーは「{dateLabel}」の情報を求めています。参考情報もすべて「{dateLabel}」のものです。")
                  .Append($"回答に書く年・月は必ず「{dateLabel}」に統一し、それ以外の年（2025年・2023年など）は絶対に書かないこと。\n\n");
            sb.Append("## 質問\n").Append(userQuery);
            userContent = sb.ToString();
        }
        messages.Add(new ChatMessage("user", userContent));
        return messages;
    }

    // Per-reference text cap so a single multi-chunk article can't swallow the
    // whole context budget.
    private const int PerRefMaxChars = 2500;

    /// <summary>Collapse chunks that share a source URL into one reference,
    /// preserving best-rank order and concatenating their text.</summary>
    private static List<ChunkResult> GroupByPost(IReadOnlyList<ChunkResult> chunks)
    {
        var order  = new List<string>();
        var groups = new Dictionary<string, List<ChunkResult>>();

        foreach (var c in chunks)
        {
            // Fall back to chunk id when a chunk has no URL, so it isn't merged away.
            var key = string.IsNullOrEmpty(c.Url) ? $"chunk:{c.ChunkId}" : c.Url;
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(key);
            }
            list.Add(c);
        }

        var result = new List<ChunkResult>(order.Count);
        foreach (var key in order)
        {
            var list = groups[key];
            var best = list[0];   // chunks arrive in ranked order; first is best
            var sb = new StringBuilder();
            foreach (var c in list)
            {
                if (sb.Length >= PerRefMaxChars) break;
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(c.Text);
            }
            var merged = sb.Length > PerRefMaxChars ? sb.ToString(0, PerRefMaxChars) : sb.ToString();
            result.Add(best with { Text = merged, Distance = list.Min(x => x.Distance) });
        }
        return result;
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
