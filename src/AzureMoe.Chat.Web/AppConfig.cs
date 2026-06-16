using AzureMoe.Chat.Web.Services;

namespace AzureMoe.Chat.Web;

/// <summary>How hard the RAG pipeline works per turn. Trades latency for depth.</summary>
public enum RetrievalMode
{
    /// <summary>1 LLM call (final answer only). Heuristic query analysis + one-hop graph.</summary>
    Fast,
    /// <summary>2 LLM calls. History-aware query rewrite, then deeper graph + answer.</summary>
    Normal,
    /// <summary>Many LLM calls. Iterative retrieve → evaluate → re-retrieve, then answer.</summary>
    Deep,
}

public sealed class AppConfig
{
    public string ManifestUrl { get; set; } = "data/manifest.json";
    public string DbBaseUrl   { get; set; } = "data/";

    // ── LLM ────────────────────────────────────────────────────────────────
    // transformers.js HuggingFace model ID.
    // Use a small Japanese-capable model that fits in browser memory.
    // Recommended options:
    //   "onnx-community/Qwen2.5-0.5B-Instruct"  — ~350 MB q4, very fast
    //   "onnx-community/Qwen2.5-1.5B-Instruct"  — ~900 MB q4, better quality
    // Standard single-file text model (Qwen2ForCausalLM) — the canonical
    // transformers.js demo model. Runs reliably on CPU/WASM (and WebGPU).
    // The previous "Qwen3.5-0.8B-ONNX-OPT" was a 256K-context VL model whose
    // unusual architecture overflowed ONNX Runtime's size calc on every backend.
    public string LlmModelId      { get; set; } = "onnx-community/Qwen2.5-0.5B-Instruct";
    // Decoder dtype. Running CPU (WASM) only — "q4" (4-bit weights, fp32
    // compute) is the best fit for CPU. The worker derives a matching
    // embed_tokens precision in buildDtype() (fp32 here) to avoid overflow.
    public string LlmDtype        { get; set; } = "q4";
    // Max new tokens for the final answer. A ceiling, not a target — generation
    // stops at EOS. Kept generous (long answers aren't truncated) but bounded:
    // if the small model degenerates, this caps how long the runaway runs on the
    // slow CPU. The Stop button covers the rest. Decoding uses light sampling
    // (see llm-worker.js) to avoid the greedy-repetition collapse.
    public int    LlmMaxNewTokens { get; set; } = 4096;
    // Budgets for the auxiliary LLM steps (query rewrite, Deep-mode sufficiency
    // evaluation). These finish in a line or two (stop at EOS).
    public int    LlmRewriteMaxTokens { get; set; } = 512;
    public int    LlmEvalMaxTokens    { get; set; } = 512;

    // ── Embedding ──────────────────────────────────────────────────────────
    // Must match the model used during ingest (same vector space).
    public string EmbeddingModelId { get; set; } = "Xenova/multilingual-e5-small";

    // ── RAG ───────────────────────────────────────────────────────────────
    public RetrievalMode RetrievalMode { get; set; } = RetrievalMode.Normal;
    // Base for the per-mode final reference count. After the relevance re-rank +
    // cutoff, fewer high-relevance references beat many noisy ones, so keep this
    // modest (precision over recall) — see RagPipeline.OptionsFor.
    public int    RagTopK         { get; set; } = 6;
    public int    MaxContextChars { get; set; } = 6000;
    // How many recent conversation turns to feed the model. Kept small so the
    // current question + retrieved context aren't drowned out by old history.
    public int    HistoryTurns    { get; set; } = 3;
    // Deep mode: max retrieve→evaluate rounds before forcing an answer.
    public int    DeepMaxRounds   { get; set; } = 3;
    // Run a post-generation check (all modes) that the answer is supported by the
    // retrieved context; warn the user when it isn't. Costs one short LLM call.
    public bool   VerifyGrounding { get; set; } = true;

    // ── Small-model context budget ──────────────────────────────────────────
    // Local WASM LLMs (2B class) suffer from "lost in the middle": they attend
    // mainly to the beginning and end of their context, missing chunks in the
    // middle. Tighter budgets keep the relevant text within the model's effective
    // attention range. HTTP mode (20B+) uses the standard values above.
    public int    LocalRagTopK         { get; set; } = 3;    // references (vs 6 for HTTP)
    public int    LocalMaxContextChars { get; set; } = 2500; // total chars  (vs 6000)
    public int    LocalPerRefMaxChars  { get; set; } = 800;  // per-ref cap  (vs 2500)

    // System prompt. Persona ("あずも", an Azure-loving guide) + strict GraphRAG
    // answering rules. Override in wwwroot/appsettings.json. A calmer, less
    // moe alternative is kept in comments below for easy swapping.
    public string SystemPrompt    { get; set; } =
        """
        あなたは Azure とクラウドに詳しいアシスタントです。

        ## 最優先ルール（絶対厳守）
        - 「参考情報」に明記されている事実だけを答える。書かれていない事実・製品名・数値・日付・URLを作り出さない。
        - 自分の知識や一般論で補完したり、推測で話を広げたりしない。参考情報に無いことは「参考情報には載っていなかったよ、先輩」とだけ言う。
        - 日付・時期は参考情報に書かれたものだけを使い、別の年月に変えない。
        - 根拠にした記事は本文中に [1] [2] の番号だけで引用する（URLや参考記事一覧は書かない。システムが別に表示する）。

        ## 簡潔さ（重要）
        - 聞かれたことにだけ、ふつうは1〜3文で答える。背景説明・前置き・言い換え・まとめは不要。
        - 挨拶（「こんにちは」など）や「他にお手伝いできることはありますか？」のような社交辞令・締めの定型句は書かない。
        - 回答は1回分だけ。会話の続きや「User:」「Message」などの発言者タグ・HTMLコメントは出力しない。
        """;
}
