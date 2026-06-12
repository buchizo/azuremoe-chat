# POC-3: 埋め込みモデル一致検証 — 結果: 成功 (2026-06-13)

取り込み側 (.NET / Microsoft.ML.OnnxRuntime) とチャット側 (transformers.js) で
multilingual-e5-small の埋め込みが一致するかの検証。**全 5 テキスト (日英) で
トークン ID 完全一致、コサイン類似度 1.00000000。**

## 実行方法

```powershell
# モデル取得 (initial setup): Xenova/multilingual-e5-small から
#   config.json / tokenizer.json / tokenizer_config.json / onnx/model.onnx
# を model/Xenova/multilingual-e5-small/ に配置 (.gitignore 済み)
cd dotnet && dotnet run        # → ../embeddings-dotnet.json
cd ../web && npm install && npm test   # transformers.js で同一テキストを埋め込み比較
```

## 構成のポイント (本番にそのまま流用)

- **両側で同一の `tokenizer.json` (HF tokenizers 形式) を使う**:
  - .NET: `Tokenizers.DotNet` (HF tokenizers の Rust バインディング) — `Encode()` が
    special tokens (`<s>`/`</s>`) も含めて transformers.js と完全一致の ID を返す
  - JS: transformers.js の `AutoTokenizer` (同じ tokenizer.json)
- ONNX モデルも同一ファイル (`onnx/model.onnx`, fp32, 470MB)。
  JS 側は `env.localModelPath` + `dtype: "fp32"` でローカル参照
- 後処理: mean pooling (attention mask) → L2 normalize。
  transformers.js の `{pooling: "mean", normalize: true}` と同じ実装を C# に書いて一致確認済み
- e5 系の流儀: クエリは `query: `、文書は `passage: ` プレフィックス
- モデルの入力は `input_ids` / `attention_mask` / `token_type_ids` (全ゼロ) の3つ

## 本番への含意

- 取り込みツールはこの dotnet 実装をほぼそのまま `IEmbedder` として移植できる
- ブラウザ側は同じモデルファイルを R2 から配信すれば、デスクトップで作った
  Chunk 埋め込みとブラウザでのクエリ埋め込みが同一ベクトル空間になる (検証済み)
- 量子化版 (q8, ~120MB) を使う場合は両側で同時に切り替えて再検証すること
