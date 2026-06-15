using System.ClientModel;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using AzureMoe.Chat.Core;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Calls a local OpenAI-compatible LLM to extract entities, relationships,
/// and Azure service names from a Japanese blog chunk.
///
/// JSON mode (<c>response_format: json_object</c>) is used together with a
/// schema description embedded in the system prompt so the model returns a
/// machine-readable structure regardless of which local server is used
/// (Ollama, LM Studio, llama.cpp, vLLM, …).
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

    public async Task<Extraction> ExtractAsync(string chunkText, CancellationToken ct = default)
    {
        List<ChatMessage> messages =
        [
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(chunkText),
        ];
        var completionOptions = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        };

        try
        {
            var result = await _chat.CompleteChatAsync(messages, completionOptions, ct);
            var json   = result.Value?.Content?.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json)) return Empty;

            var extraction = JsonSerializer.Deserialize<Extraction>(json, JsonOpts);
            if (extraction == null) return Empty;

            // Guard against null collections from partial JSON (LLM may omit fields).
            return extraction with
            {
                Entities      = extraction.Entities      ?? [],
                Relationships = extraction.Relationships ?? [],
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return Empty; }
    }

    private static readonly Extraction Empty = new([], [], []);

    public void Dispose() { }
}
