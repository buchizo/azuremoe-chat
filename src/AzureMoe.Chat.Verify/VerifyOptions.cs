namespace AzureMoe.Chat.Verify;

public sealed class VerifyOptions
{
    /// <summary>
    /// 検索対象の .lbdb ファイルパス。
    /// 未指定の場合は OutDir から最新ファイルを自動検出。
    /// </summary>
    public string DbPath { get; set; } = "";

    /// <summary>DbPath 未指定時の自動検索ディレクトリ。</summary>
    public string OutDir { get; set; } = "out";

    /// <summary>埋め込みモデルの HuggingFace ID。DB 構築時と同じものを指定する (manifest.json の embeddingModel)。</summary>
    public string EmbeddingModel { get; set; } = AzureMoe.Chat.Core.EmbeddingProfile.Default.ModelId;

    /// <summary>埋め込みモデルの dtype。DB 構築時と同じものを指定する (manifest.json の embeddingDtype)。</summary>
    public string EmbeddingDtype { get; set; } = AzureMoe.Chat.Core.GraphSchema.EmbeddingDtype;

    /// <summary>モデルのディレクトリ。未指定時は model/{EmbeddingModel}。</summary>
    public string? ModelDir { get; set; }

    /// <summary>返す検索結果数。</summary>
    public int TopK { get; set; } = 5;
}
