using System.Text;
using System.Text.RegularExpressions;
using AzureMoe.Chat.Web;

namespace AzureMoe.Chat.Web.Services;

/// <summary>
/// Orchestrates a RAG turn. Retrieval is graph- and date-aware (handled by
/// <see cref="RagInterop"/> / rag-worker.js); the depth of the turn is set by
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
    private readonly RagInterop    _ragInterop;
    private readonly QueryAnalyzer _analyzer;
    private readonly ILlmEngine    _llm;
    private readonly AppConfig     _cfg;

    public RagPipeline(RagInterop ragInterop, QueryAnalyzer analyzer, ILlmEngine llm, AppConfig cfg)
    {
        _ragInterop = ragInterop;
        _analyzer   = analyzer;
        _llm        = llm;
        _cfg        = cfg;
    }

    // ── Mode-aware context budget ───────────────────────────────────────────
    // HTTP mode (20B+ models) gets full settings; the local model gets the
    // Local* budgets. On the WASM fallback (no WebGPU) prefill runs at tens of
    // tokens/sec, so the budget is clamped further to keep time-to-first-token
    // tolerable.
    private bool IsHttpMode         => _llm.Device == "http";
    private bool IsWasmDevice       => _llm.Device == "wasm";
    private const int WasmMaxContextChars = 2800;
    private const int WasmMaxTopK         = 4;
    private int  EffRagTopK         => IsHttpMode ? _cfg.RagTopK
                                     : IsWasmDevice ? Math.Min(_cfg.LocalRagTopK, WasmMaxTopK)
                                     : _cfg.LocalRagTopK;
    private int  EffMaxContextChars => IsHttpMode ? _cfg.MaxContextChars
                                     : IsWasmDevice ? Math.Min(_cfg.LocalMaxContextChars, WasmMaxContextChars)
                                     : _cfg.LocalMaxContextChars;
    private int  EffPerRefMaxChars  => IsHttpMode ? 2500                  : _cfg.LocalPerRefMaxChars;

    public async ValueTask<IReadOnlyList<ChunkResult>> RunAsync(
        string userQuery,
        IReadOnlyList<ChatMessage> history,
        Func<string, ValueTask> onToken,
        Action<string>? onCompleted = null,
        Action<string>? onStatus = null,
        Action<string>? onWarning = null,
        Action? onStreamReset = null,
        Action<string>? onDebug = null,
        CancellationToken ct = default)
    {
        void Status(string s) => onStatus?.Invoke(s);
        void Debug(string s)  => onDebug?.Invoke(s);

        // RAG worker not initialized (e.g. mobile device where loading is skipped).
        if (!_ragInterop.IsInitialised)
        {
            onCompleted?.Invoke(
                "RAG が起動していないため検索できません。\n" +
                "外部 LLM を使う場合は /llm コマンドでエンドポイントを設定できます。");
            return [];
        }

        var trimmed = TrimHistory(history);

        IReadOnlyList<ChatMessage> generationHistory =
            _cfg.RetrievalMode == RetrievalMode.Deep ? trimmed : [];

        var sources = _cfg.RetrievalMode switch
        {
            RetrievalMode.Deep => await RetrieveDeepAsync(userQuery, trimmed, Status, onDebug, ct),
            _                  => await RetrieveOnceAsync(userQuery, trimmed, Status, onDebug, ct),
        };

        // Merge chunks from the same blog post (same URL) into one numbered
        // reference, so both the context the model cites and the sources shown
        // to the user list each article once.
        var references = GroupByPost(sources, EffPerRefMaxChars);
        var context    = BuildContext(references, EffMaxContextChars);

        // No grounding material → don't let the model answer from its own
        // knowledge. Return a canned "not found" instead of risking a hallucination.
        if (string.IsNullOrWhiteSpace(context))
        {
            onCompleted?.Invoke(NoContextMessage);
            return references;
        }

        // LLM not loaded (e.g. mobile device where loading is skipped to avoid OOM).
        if (!_llm.IsLoaded)
        {
            onCompleted?.Invoke(
                "LLM が起動していないため回答は生成されません。下の参考記事をご参照ください。\n" +
                "外部 LLM を使う場合は /llm コマンドでエンドポイントを設定できます。");
            return references;
        }

        // Streamed generation. The stream is a live PREVIEW only — we commit the
        // final answer (onCompleted) ourselves after the grounding check, so a
        // failed attempt can be regenerated without showing two answers.
        async ValueTask<string> GenerateAsync(List<ChatMessage> msgs)
        {
            onStreamReset?.Invoke();           // clear any previous preview
            if (onDebug is not null) Debug(FormatPromptDebug("▶ LLM 回答生成", msgs));
            var text = "";
            await _llm.ChatAsync(msgs, onToken, full => text = full, _cfg.LlmMaxNewTokens, ct);
            text = NormalizeCitations(SanitizeAnswer(text), references.Count);
            if (onDebug is not null) Debug(FormatResponseDebug("LLM 応答", text));
            return text;
        }

        // If the user's question carries a date window, pass its label so the
        // prompt can pin the answer's dates (small models otherwise drift to
        // years from their training data). Only for an explicit window (年/月);
        // a vague "最近" must not turn into "年月を『最近』に統一".
        var qDate     = _analyzer.Analyze(userQuery).Date;
        var dateLabel = qDate is { Hard: true } ? qDate.Label : null;

        Status("あずもが回答を考えています…");
        var answer   = await GenerateAsync(BuildMessages(generationHistory, userQuery, context, dateLabel, strict: false));
        var grounded = true;

        // Grounding check so the model can't quietly fall back to general
        // knowledge. On failure, regenerate once with a stricter prompt.
        // Skipped on the WASM fallback: the extra prefill pass costs 25-60 s
        // there, versus a few seconds on WebGPU.
        var verifyGrounding = _cfg.VerifyGrounding && !IsWasmDevice;
        if (verifyGrounding && !string.IsNullOrWhiteSpace(answer))
        {
            Status("回答が参考情報に基づいているか確認しています…");
            if (onDebug is not null) Debug("[debug] ▶ グラウンディング確認");
            grounded = await IsGroundedAsync(answer, context, ct, onDebug);
            if (onDebug is not null) Debug($"[debug] グラウンディング判定: {(grounded ? "OK" : "NG")}");

            if (!grounded)
            {
                Status("根拠に厳密に基づいて回答し直しています…");
                var retry = await GenerateAsync(BuildMessages(generationHistory, userQuery, context, dateLabel, strict: true));
                if (!string.IsNullOrWhiteSpace(retry))
                {
                    answer   = retry;
                    if (onDebug is not null) Debug("[debug] ▶ グラウンディング再確認");
                    grounded = await IsGroundedAsync(retry, context, ct, onDebug);
                    if (onDebug is not null) Debug($"[debug] グラウンディング判定: {(grounded ? "OK" : "NG")}");
                }
            }
        }

        // Commit the final answer once, then warn if it still isn't grounded.
        onCompleted?.Invoke(answer);
        if (verifyGrounding && !grounded)
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

    // Strip URLs from chunk body text before sending to the LLM.
    // Markdown inline links [text](url) → keep the anchor text, discard the URL.
    // Bare http(s) URLs are removed entirely. Redundant whitespace is collapsed.
    private static readonly Regex _mdLink    = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex _bareUrl   = new(@"https?://\S+",          RegexOptions.Compiled);
    private static readonly Regex _multiSpace = new(@"[ \t]{2,}",            RegexOptions.Compiled);

    private static string StripUrls(string text)
    {
        text = _mdLink.Replace(text, "$1");
        text = _bareUrl.Replace(text, "");
        text = _multiSpace.Replace(text, " ");
        return text;
    }

    private static readonly TimeZoneInfo _jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private static string FormatDateJst(string raw)
    {
        if (DateTimeOffset.TryParse(raw, out var dto))
            return TimeZoneInfo.ConvertTime(dto, _jst).ToString("yyyy/MM/dd");
        return raw.Length >= 10 ? raw[..10] : raw;
    }

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

    // Citation variants a small model produces: 【1】, ［1］, [ 1 ], [1, 2], [1、2].
    private static readonly Regex _citationVariant = new(
        @"[\[［【]\s*(\d+(?:\s*[,、]\s*\d+)*)\s*[\]］】]", RegexOptions.Compiled);

    /// <summary>Normalise citation markers to the canonical "[1] [2]" form and
    /// drop ordinals beyond the actual reference count (hallucinated numbers).
    /// Missing citations are left as-is — the UI's source list stays
    /// authoritative; model citations are best-effort decoration.</summary>
    private static string NormalizeCitations(string text, int refCount)
    {
        if (string.IsNullOrEmpty(text) || refCount <= 0) return text;
        return _citationVariant.Replace(text, m =>
        {
            var nums = m.Groups[1].Value
                .Split([',', '、'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0)
                .Where(n => n >= 1 && n <= refCount)
                .Distinct();
            return string.Join(" ", nums.Select(n => $"[{n}]"));
        });
    }

    // Max chars of context to send to the grounding check. The full 6 000-char
    // context would force the model to process ~1 500 tokens just to output "OK"
    // or "NG". Truncating to 2 000 chars cuts ~1 000 input tokens per check while
    // still giving the model enough text to spot obvious hallucinations.
    private const int GroundingContextCap = 2000;

    /// <summary>Second LLM pass: is the answer supported only by the context?
    /// Deliberately lenient on ambiguity (don't cry wolf) but flags clear drift
    /// into facts/names/dates absent from the context.</summary>
    private async ValueTask<bool> IsGroundedAsync(string answer, string context, CancellationToken ct,
        Action<string>? onDebug = null)
    {
        var ctx = context.Length > GroundingContextCap ? context[..GroundingContextCap] : context;
        var messages = new List<ChatMessage>
        {
            new("system",
                "あなたは厳密な校正者です。『回答』が『参考情報』だけで裏付けられるか判定してください。" +
                "参考情報に書かれていない事実・製品名・数値・日付が回答に含まれる場合は不合格です。" +
                "裏付けられるなら『OK』、そうでなければ『NG』とだけ出力してください。"),
            new("user", $"## 参考情報\n{ctx}\n\n## 回答\n{answer}\n\n判定（OK か NG のみ）:"),
        };

        if (onDebug is not null) onDebug(FormatPromptDebug("LLM グラウンディング確認プロンプト", messages));
        var raw = (await _llm.CompleteAsync(messages, _cfg.LlmEvalMaxTokens, ct)).ToUpperInvariant();
        // Treat only an explicit NG (without a competing OK) as ungrounded.
        return !(raw.Contains("NG") && !raw.Contains("OK"));
    }

    // ── Fast / Normal: one recall pass ────────────────────────────────────────

    private async ValueTask<IReadOnlyList<ChunkResult>> RetrieveOnceAsync(
        string userQuery, IReadOnlyList<ChatMessage> history,
        Action<string> status, Action<string>? onDebug, CancellationToken ct)
    {
        var searchQuery = await MaybeRewriteQueryAsync(userQuery, history, status, onDebug, ct);
        status("関連する記事を検索しています…");
        if (onDebug is not null) onDebug("[debug] ▶ ベクトル検索・グラフ展開");
        var opt     = OptionsFor(_cfg.RetrievalMode);
        var origQ   = _analyzer.Analyze(userQuery);
        var searchQ = searchQuery == userQuery ? origQ : _analyzer.Analyze(searchQuery);
        return await _ragInterop.RetrieveAsync(userQuery, searchQuery, origQ, searchQ, opt, _cfg.RetrievalMode, onDebug, ct);
    }

    // ── Deep: multi-round recall (all rounds handled inside rag-worker.js) ────

    private async ValueTask<IReadOnlyList<ChunkResult>> RetrieveDeepAsync(
        string userQuery, IReadOnlyList<ChatMessage> history,
        Action<string> status, Action<string>? onDebug, CancellationToken ct)
    {
        var searchQuery = await MaybeRewriteQueryAsync(userQuery, history, status, onDebug, ct);
        status("関連する記事を検索しています…");
        if (onDebug is not null) onDebug("[debug] ▶ ベクトル検索・グラフ展開 (Deep)");
        var opt     = OptionsFor(RetrievalMode.Deep);
        var origQ   = _analyzer.Analyze(userQuery);
        var searchQ = searchQuery == userQuery ? origQ : _analyzer.Analyze(searchQuery);
        return await _ragInterop.RetrieveAsync(userQuery, searchQuery, origQ, searchQ, opt, RetrievalMode.Deep, onDebug, ct);
    }

    // ── History-aware query rewrite (conditional) ─────────────────────────────

    // Follow-up phrasings whose raw text is a useless search key ("それの料金は？").
    private static readonly Regex _anaphoraMarker = new(
        "それ|これ|あれ|その|この|さっき|先ほど|前の|他に|ほかに|続き|もっと",
        RegexOptions.Compiled);

    /// <summary>Rewrite the query into a self-contained search key using the
    /// local LLM — but only when there is history AND the query looks anaphoric
    /// (an unconditional rewrite costs an extra LLM round-trip per question and
    /// can mangle an already-good query). Falls back to the raw query on any
    /// suspicious output.</summary>
    private async ValueTask<string> MaybeRewriteQueryAsync(
        string userQuery, IReadOnlyList<ChatMessage> history,
        Action<string> status, Action<string>? onDebug, CancellationToken ct)
    {
        if (history.Count == 0 || !_llm.IsLoaded) return userQuery;

        var anaphoric = _anaphoraMarker.IsMatch(userQuery)
                        || _analyzer.Analyze(userQuery).Keywords.Count == 0
                        || userQuery.Length < 12;
        if (!anaphoric) return userQuery;

        status("質問を検索用に整理しています…");
        var convo = new StringBuilder();
        foreach (var m in history.Skip(Math.Max(0, history.Count - 4)))   // last 2 turns
        {
            var content = m.Content.Length > 200 ? m.Content[..200] + "…" : m.Content;
            convo.Append(m.Role == "user" ? "ユーザー: " : "アシスタント: ").Append(content).Append('\n');
        }

        var messages = new List<ChatMessage>
        {
            new("system",
                "直前の会話をふまえて、ユーザーの質問を単独で意味が通る日本語の検索クエリ1行に書き換えてください。" +
                "「それ」「この」などの指示語は会話に出てきた具体的な名称に置き換えます。" +
                "説明や引用符は書かず、検索クエリだけを出力してください。"),
            new("user", $"## 会話\n{convo}\n## 質問\n{userQuery}\n\n## 検索クエリ"),
        };

        try
        {
            var raw = (await _llm.CompleteAsync(messages, _cfg.LlmRewriteMaxTokens, ct)).Trim();
            var line = raw.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault() ?? "";
            line = line.Trim('"', '「', '」', '『', '』', ' ');
            // Guards: empty, runaway, or leaked role tags → keep the raw query.
            var ok = line.Length is > 0 and <= 120
                     && !line.Contains("ユーザー:") && !line.Contains("アシスタント:");
            if (onDebug is not null)
                onDebug($"[debug] クエリリライト: \"{userQuery}\" → \"{(ok ? line : "(棄却: " + line + ")")}\"");
            return ok ? line : userQuery;
        }
        catch
        {
            return userQuery;   // rewrite is best-effort; retrieval must not fail
        }
    }

    // ── Debug formatting ───────────────────────────────────────────────────

    private const int DebugHead = 600;
    private const int DebugTail = 300;

    // Show head + tail so long messages (context before the question) don't hide
    // the "## 質問" section that always lives at the end of the user turn.
    private static string Trunc(string s)
    {
        if (s.Length <= DebugHead + DebugTail) return s;
        return s[..DebugHead] + $"\n  …({s.Length - DebugHead - DebugTail}文字省略)…\n  " + s[^DebugTail..];
    }

    private static string FormatPromptDebug(string label, IEnumerable<ChatMessage> msgs)
    {
        var sb = new StringBuilder($"[debug] {label}\n");
        foreach (var m in msgs)
            sb.Append($"  [{m.Role}] {Trunc(m.Content)}\n");
        return sb.ToString().TrimEnd();
    }

    private static string FormatResponseDebug(string label, string response) =>
        $"[debug] {label}: \"{Trunc(response)}\"";


    // ── Prompt assembly ────────────────────────────────────────────────────────

    private RetrievalOptions OptionsFor(RetrievalMode mode) => mode switch
    {
        // Fast: pure vector, no graph traversal, fewest sources → quickest.
        RetrievalMode.Fast => new RetrievalOptions(
            FinalTopK: Math.Max(2, EffRagTopK - 1), VectorTopK: 12, UseGraph: false,
            IncludeRelated: false, ExpansionLimit: 0, DateOverFetch: 300),
        // Deep: wide recall + related-entity hop. Precision comes from the final
        // rank against the original question, so recall can be generous.
        RetrievalMode.Deep => new RetrievalOptions(
            FinalTopK: EffRagTopK, VectorTopK: 30, UseGraph: true,
            IncludeRelated: true, ExpansionLimit: 14, DateOverFetch: 600),
        // Normal: balanced graph expansion.
        _ => new RetrievalOptions(
            FinalTopK: EffRagTopK, VectorTopK: 24, UseGraph: true,
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
        var now  = DateTime.Now;
        var days = new[] { "日", "月", "火", "水", "木", "金", "土" };
        var sysPrompt = _cfg.SystemPrompt +
            $"\n\n現在の日時: {now:yyyy年M月d日}（{days[(int)now.DayOfWeek]}）";
        var messages = new List<ChatMessage> { new("system", sysPrompt) };
        messages.AddRange(history);

        // Question at both ends: stated first so the model anchors on intent
        // before reading context, and restated after the context so it sits at
        // the generation point (small models weight the end of the prompt most).
        // Context is labelled "システム取得" to distinguish it from user input.
        string userContent;
        if (string.IsNullOrWhiteSpace(context))
        {
            userContent = userQuery;
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append("## 質問（ユーザー入力）\n").Append(userQuery).Append("\n\n");
            sb.Append("## 参考情報（システム取得）\n\n").Append(context).Append("\n\n");
            if (strict)
                sb.Append("## 重要\n前回の回答は参考情報で裏付けられていませんでした。今回は参考情報に明記された内容だけを、")
                  .Append("該当する記事番号 [1] [2] を本文中に引用しながら答えてください。")
                  .Append("参考情報に書かれていないことは一切書かず、該当情報が無ければ「参考情報には載っていなかったよ、先輩」とだけ答えること。\n\n");
            if (!string.IsNullOrEmpty(dateLabel))
                sb.Append($"## 重要な制約\nユーザーは「{dateLabel}」の情報を求めています。参考情報もすべて「{dateLabel}」のものです。")
                  .Append($"回答に書く年・月は必ず「{dateLabel}」に統一し、それ以外の年（2025年・2023年など）は絶対に書かないこと。\n\n");
            sb.Append("## 上記の参考情報だけを使って、次の質問に答えてください\n").Append(userQuery);
            userContent = sb.ToString().TrimEnd();
        }
        messages.Add(new ChatMessage("user", userContent));
        return messages;
    }

    /// <summary>Collapse chunks that share a source URL into one reference,
    /// preserving best-rank order and concatenating their text.
    /// <paramref name="perRefMaxChars"/> caps each reference's text independently.</summary>
    private static List<ChunkResult> GroupByPost(IReadOnlyList<ChunkResult> chunks, int perRefMaxChars)
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
            var seenBlocks = new HashSet<string>();
            foreach (var c in list)
            {
                if (sb.Length >= perRefMaxChars) break;
                // Small-to-big: prefer the richer neighbour-window context over the
                // bare retrieval chunk. Sibling bullets from the same section share
                // an identical ContextText, so dedupe before concatenating.
                var block = StripUrls(string.IsNullOrEmpty(c.ContextText) ? c.Text : c.ContextText);
                if (!seenBlocks.Add(block)) continue;
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(block);
            }
            var merged = sb.Length > perRefMaxChars ? sb.ToString(0, perRefMaxChars) : sb.ToString();
            result.Add(best with { Text = merged, Distance = list.Min(x => x.Distance) });
        }
        return result;
    }

    private static string BuildContext(IReadOnlyList<ChunkResult> chunks, int maxContextChars)
    {
        // Select references in rank order until the budget is exhausted. The
        // header carries only what the model needs to cite (number/title/date);
        // similarity scores and URLs are display concerns, not prompt material.
        var picked = new List<string>();
        var used   = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            var c     = chunks[i];
            var block = $"### [{i + 1}] {c.Title}（{FormatDateJst(c.Date)}）\n{c.Text}";
            if (used + block.Length > maxContextChars && used > 0)
                break;
            picked.Add(block);
            used += block.Length;
        }

        // Emit least-relevant first: the strongest evidence sits right before the
        // restated question at the end of the user turn (small-model recency
        // bias). Numbering stays rank-based so [n] matches the UI's source list.
        picked.Reverse();
        return string.Join("\n\n---\n\n", picked);
    }
}
