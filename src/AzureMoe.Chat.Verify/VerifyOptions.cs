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

    /// <summary>E5 モデルのディレクトリ。</summary>
    public string ModelDir { get; set; } = "model/Xenova/multilingual-e5-small";

    /// <summary>返す検索結果数。</summary>
    public int TopK { get; set; } = 5;
}
