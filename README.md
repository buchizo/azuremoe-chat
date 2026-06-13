# azuremoe-chat

WordPress ブログ記事を知識ソースとした GraphRAG チャットアプリ。
Blazor WASM + LadybugDB + WebLLM をブラウザ内で動かし、外部 API 不要でオフライン動作する。

> **現在の状態**: Phase 1 (インジェストツール) 完成、Phase 2 (チャットアプリ) 着手前。

---

## リポジトリ構成

```
azuremoe-chat/
├── src/
│   ├── AzureMoe.Chat.Core/       共有ライブラリ (スキーマ定数・チャンク化)
│   ├── AzureMoe.Chat.Ingest/     インジェスト CLI ← このドキュメントのメイン
│   └── AzureMoe.Chat.Verify/     検索動作確認 CLI
├── docs/
│   └── architecture.md           設計ドキュメント
├── poc/                          Phase 0 技術検証コード
└── model/                        埋め込みモデル置き場 (git 管理外)
    └── Xenova/
        └── multilingual-e5-small/
```

---

## 前提条件

- .NET 10 SDK
- Windows x64 (LadybugDB ネイティブバインディングが win-x64 のみ)
- WordPress エクスポート XML ファイル
- エンティティ抽出用ローカル LLM (任意。`--NoLlm` でスキップ可)

---

## 埋め込みモデルのセットアップ

インジェストと検索ツールの両方が **Xenova/multilingual-e5-small** (ONNX 量子化版) を使用する。

### モデルについて

| 項目 | 値 |
|---|---|
| HuggingFace リポジトリ | [Xenova/multilingual-e5-small](https://huggingface.co/Xenova/multilingual-e5-small) |
| ベースモデル | intfloat/multilingual-e5-small (XLM-RoBERTa ベース) |
| パラメータ数 | 約 117M |
| 埋め込み次元 | 384 |
| 最大シーケンス長 | 512 トークン |
| モデルファイルサイズ | `model_quantized.onnx` (INT8) — 約 118 MB |
| 言語 | 100 言語対応 (日本語含む) |

### ダウンロード手順

以下のファイルを `model/Xenova/multilingual-e5-small/` に配置する。

**必要なファイル:**

```
model/Xenova/multilingual-e5-small/
├── tokenizer.json                 トークナイザー設定
├── tokenizer_config.json          トークナイザー設定
├── special_tokens_map.json        特殊トークン定義
├── sentencepiece.bpe.model        SentencePiece モデル (~4.9 MB)
└── onnx/
    └── model_quantized.onnx       INT8 量子化モデル (~118 MB)  ← 必須
```

> `model.onnx` (FP32 フル精度, ~470 MB) も使用可能。`model_quantized.onnx` が優先される。

**方法 1: huggingface-hub (推奨)**

```bash
pip install huggingface-hub
huggingface-cli download Xenova/multilingual-e5-small \
  tokenizer.json tokenizer_config.json special_tokens_map.json sentencepiece.bpe.model \
  onnx/model_quantized.onnx \
  --local-dir model/Xenova/multilingual-e5-small
```

**方法 2: 手動ダウンロード**

HuggingFace の [Files タブ](https://huggingface.co/Xenova/multilingual-e5-small/tree/main) から上記ファイルを個別にダウンロードし、フォルダ構成通りに配置する。

---

## WordPress エクスポートファイルの準備

1. WordPress 管理画面 → **ツール → エクスポート**
2. 「すべてのコンテンツ」または「投稿」を選択してエクスポート
3. ダウンロードした `.xml` ファイルを `.tmp/` フォルダに配置

```
.tmp/
└── wordpress.2026-06-14.xml
```

---

## インジェストツール (AzureMoe.Chat.Ingest)

WordPress XML を読み込んでエンティティを抽出し、GraphDB を構築して `out/` に出力する。

### 基本的な実行

```bash
# LLM なし (埋め込みのみ, 動作確認用)
dotnet run --project src/AzureMoe.Chat.Ingest -- --NoLlm --SkipR2

# LLM あり (Ollama)
dotnet run --project src/AzureMoe.Chat.Ingest -- --SkipR2

# R2 アップロードあり
dotnet run --project src/AzureMoe.Chat.Ingest
```

### 引数一覧

設定の優先順位: **コマンドライン引数 > 環境変数 > appsettings.json > デフォルト値**

#### 入力

| 引数 | 環境変数 | デフォルト | 説明 |
|---|---|---|---|
| `--XmlDir` | — | `.tmp` | WordPress エクスポート XML が入ったディレクトリ |
| `--MaxPosts` | — | `0` (全件) | 処理する最大投稿数。動作確認時は `10` など小さい値に |

#### 埋め込みモデル

| 引数 | 環境変数 | デフォルト | 説明 |
|---|---|---|---|
| `--ModelDir` | — | `model/Xenova/multilingual-e5-small` | ONNX モデルのディレクトリパス |

#### ローカル LLM (エンティティ抽出)

エンティティ・Azure サービス名の抽出に使用する OpenAI 互換エンドポイント。
`--NoLlm` を指定するとこのステップをスキップする。

| 引数 | 環境変数 | デフォルト | 説明 |
|---|---|---|---|
| `--NoLlm` | — | `false` | エンティティ抽出をスキップ (タグのみでグラフ構築) |
| `--LlmBaseUrl` | `LLM_BASE_URL` | `http://localhost:11434/v1` | LLM エンドポイントのベース URL |
| `--LlmModel` | `LLM_MODEL` | `qwen3:8b` | モデル名 (サーバーに読み込まれているモデル) |
| `--LlmApiKey` | `LLM_API_KEY` | (なし) | API キー。ローカルサーバーは通常不要 |

**主要な LLM サーバー別設定例:**

| サーバー | `LlmBaseUrl` | `LlmModel` 例 |
|---|---|---|
| Ollama | `http://localhost:11434/v1` | `qwen3:8b`, `llama3.1:8b` |
| LM Studio | `http://localhost:1234/v1` | ロード中のモデル名 |
| llama.cpp server | `http://localhost:8080/v1` | (引数不要のことも多い) |

#### 出力

| 引数 | 環境変数 | デフォルト | 説明 |
|---|---|---|---|
| `--OutDir` | — | `out` | `.lbdb` ファイルと `manifest.json` の出力先 |
| `--SkipR2` | — | `false` | R2 アップロードをスキップ。ローカル確認時は指定推奨 |

#### Cloudflare R2 アップロード (任意)

4 項目すべて設定されている場合のみアップロードを実行する。
シークレット情報のため **環境変数での設定を推奨**。

| 引数 | 環境変数 | 説明 |
|---|---|---|
| `--R2AccountId` | `R2_ACCOUNT_ID` | Cloudflare アカウント ID |
| `--R2AccessKeyId` | `R2_ACCESS_KEY_ID` | R2 の Access Key ID |
| `--R2SecretAccessKey` | `R2_SECRET_ACCESS_KEY` | R2 の Secret Access Key |
| `--R2Bucket` | `R2_BUCKET` | R2 バケット名 |

### appsettings.json での設定例

機密情報以外をファイルで管理したい場合:

```json
{
  "XmlDir": ".tmp",
  "ModelDir": "model/Xenova/multilingual-e5-small",
  "LlmBaseUrl": "http://localhost:11434/v1",
  "LlmModel": "qwen3:8b",
  "OutDir": "out",
  "SkipR2": true
}
```

### 出力ファイル

```
out/
├── blog-20260614.lbdb    GraphDB ファイル (Ladybug 0.17.x 形式)
└── manifest.json         メタデータ (モデル情報・件数・SHA-256)
```

---

## 検索確認ツール (AzureMoe.Chat.Verify)

構築した GraphDB に対してベクトル検索を対話的に試せるツール。

### 実行

```bash
# out/ から最新 .lbdb を自動検出
dotnet run --project src/AzureMoe.Chat.Verify

# DB を明示指定
dotnet run --project src/AzureMoe.Chat.Verify -- --DbPath out/blog-20260614.lbdb

# 結果件数を変える
dotnet run --project src/AzureMoe.Chat.Verify -- --TopK 10
```

### 引数一覧

| 引数 | デフォルト | 説明 |
|---|---|---|
| `--DbPath` | (自動検出) | 検索対象の `.lbdb` ファイルパス |
| `--OutDir` | `out` | `DbPath` 未指定時に最新 `.lbdb` を探すディレクトリ |
| `--ModelDir` | `model/Xenova/multilingual-e5-small` | ONNX モデルのディレクトリパス |
| `--TopK` | `5` | 返す検索結果の件数 |

### 起動後の操作

```
> 検索クエリを入力して Enter
> \stats    統計情報を再表示
> q         終了
```
