using System.ClientModel;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using AzureMoe.Chat.Core;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Calls a local OpenAI-compatible LLM to extract entities, relationships,
/// and Azure service names from a Japanese blog chunk.
/// JSON structure is enforced via the system prompt only — no response_format
/// constraint — so any OpenAI-compatible server works regardless of JSON-mode support.
/// </summary>
public sealed class EntityExtractor : IDisposable
{
    private readonly ChatClient _chat;

    public EntityExtractor(string baseUrl, string model, string? apiKey = null)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        var client  = new OpenAIClient(new ApiKeyCredential(apiKey ?? "dummy"), options);
        _chat = client.GetChatClient(model);
    }

    private const string SystemPrompt = """
        あなたは Azure Update ブログの日本語テキストから知識グラフ用の情報を抽出する専門家です。
        必ず以下の JSON 形式 **のみ** で応答してください。説明文や前置きは不要です。

        {
          "azureServices": ["Azure Functions", "Azure Container Apps"],
          "entities": [
            {"name": "エンティティ名", "type": "種別", "description": "説明"}
          ],
          "relationships": [
            {"source": "起点名", "target": "終点名", "description": "関係の説明"}
          ]
        }

        ## azureServices (最重要)
        - テキストに登場する Azure / Microsoft サービス・製品名をすべて正式名称で列挙
        - 例: "Azure Functions", "Azure Container Apps", "Microsoft Entra ID", "GitHub Actions"
        - 通称・略称は正式名称に統一 ("Entra" → "Microsoft Entra ID")
        - 登場しない場合は []

        ## entities
        - Azure サービス以外の重要なエンティティ (人名・組織・技術概念・機能名・規格名など)
        - 一般的すぎる語は除外 ("機能" "方法" "設定" "情報" など)
        - type 例: 人物 / 組織 / 技術 / 機能 / 規格 / 概念
        - Azure サービスは entities には含めない

        ## relationships
        - entities 内のエンティティ間の意味的なつながり
        - source / target は entities に含まれる name と一致させる
        - description は日本語で簡潔に

        重要な情報がない場合は各フィールドを空配列にしてください。
        Groundingで得られた内容に含まれないURLを勝手に生成してはいけません。
        """;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private int _parseFailures;

    /// <summary>チャンク数ベースの JSON パース失敗数（1回のリトライ後も失敗したもの）。
    /// ingest 終了時にサマリ表示し、グラフエッジの静かな欠落を可視化する。</summary>
    public int ParseFailureCount => _parseFailures;

    public async Task<Extraction> ExtractAsync(string chunkText, CancellationToken ct = default)
    {
        // 失敗はグラフエッジの欠落として静かに効いてくるので、1回だけ念押し付きでリトライする。
        var extraction = await TryExtractAsync(chunkText, remind: false, ct)
                      ?? await TryExtractAsync(chunkText, remind: true,  ct);
        if (extraction is not null) return extraction;

        Interlocked.Increment(ref _parseFailures);
        Console.Error.WriteLine("\n  [warn] JSON パース失敗 (リトライ後もスキップ)");
        return new Extraction([], [], []);
    }

    private async Task<Extraction?> TryExtractAsync(string chunkText, bool remind, CancellationToken ct)
    {
        // "/no_think" は Ollama 経由の qwen3 の思考モードを無効化するソフトスイッチ。
        // 思考テキストが出力トークン予算を食って JSON が途中で切れるのを防ぐ。
        // 対応しないモデルではただの末尾テキストとして無視される。
        var user = chunkText + "\n\n/no_think";
        if (remind)
            user += "\n\n必ず JSON オブジェクトのみを出力してください。説明文・思考過程・コードフェンスは不要です。";

        List<ChatMessage> messages =
        [
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(user),
        ];

        var options = new ChatCompletionOptions { MaxOutputTokenCount = 2048 };
        var result = await _chat.CompleteChatAsync(messages, options, ct);
        var raw = result.Value?.Content?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var json = ExtractJson(StripThinking(raw));
        try
        {
            var extraction = JsonSerializer.Deserialize<Extraction>(json, JsonOpts);
            if (extraction is null) return null;

            // Guard against null collections from partial JSON (LLM may omit fields).
            return extraction with
            {
                Entities      = extraction.Entities      ?? [],
                Relationships = extraction.Relationships ?? [],
                AzureServices = extraction.AzureServices ?? [],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 思考モードのモデル (qwen3 など) が <think>...</think> を先頭に出力した場合に除去する。
    // ExtractJson の first-{ ヒューリスティックが思考テキスト内の brace を拾うのを防ぐ。
    private static string StripThinking(string raw)
    {
        var end = raw.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? raw[(end + "</think>".Length)..] : raw;
    }

    // モデルが JSON を ```json ... ``` や ``` ... ``` で囲んで返す場合に抽出する。
    // 囲みがなければ raw をそのまま返す。
    private static string ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end   = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
            return raw[start..(end + 1)];
        return raw;
    }

    public void Dispose() { }
}
