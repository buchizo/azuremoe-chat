# azuremoe-chat

WordPress ブログ記事を知識ソースとした GraphRAG チャットアプリ。
Blazor WASM + LadybugDB + WebLLM をブラウザ内で動かし、外部 API 不要でオフライン動作する。

> **現在の状態**: Phase 1 (インジェストツール) 完成、Phase 2 (チャットアプリ) 実装済み。

---

## リポジトリ構成

```
azuremoe-chat/
├── src/
│   ├── AzureMoe.Chat.Core/       共有ライブラリ (スキーマ定数・チャンク化)
│   ├── AzureMoe.Chat.Ingest/     インジェスト CLI
│   ├── AzureMoe.Chat.Verify/     検索動作確認 CLI
│   └── AzureMoe.Chat.Web/        Blazor WASM チャットアプリ (Phase 2)
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

---

## チャットアプリ (AzureMoe.Chat.Web)

Blazor WebAssembly でブラウザ内完結の GraphRAG チャット。
LadybugDB (WASM)・transformers.js をすべてブラウザ内で実行する。
以下の優先順位で LLM バックエンドを自動選択するため **どの環境でも動作する**:

1. **Chrome 組み込み AI (Gemini Nano)** — Chrome 127+ でモデルダウンロード不要
2. **transformers.js + WebGPU** — WebGPU 対応ブラウザで GPU 推論
3. **transformers.js + WASM CPU** — すべての環境で動作 (低速)

### 使用モデル

| 役割 | バックエンド | モデル / エンジン | 備考 |
|---|---|---|---|
| LLM (テキスト生成) | Chrome 組み込み AI | Gemini Nano | ダウンロード不要・最優先 |
| LLM (テキスト生成) | transformers.js | `onnx-community/Qwen3.5-0.8B-ONNX-OPT` (q4) | Chrome AI 非対応時に自動ダウンロード |
| 埋め込み | transformers.js | `Xenova/multilingual-e5-small` | 起動時に自動ダウンロード |

モデルは transformers.js が Hugging Face Hub からダウンロードし、Cache API で自動キャッシュする。
2 回目以降はオフラインでも動作する。

#### Chrome 組み込み AI を有効にする方法

> **必要な Chrome バージョン**: Chrome 138+ (Dev または Canary チャンネル)。安定版は 2026 年後半予定。

**ハードウェア要件:**
- 空きストレージ 22 GB 以上
- RAM 16 GB 以上、または GPU VRAM 4 GB 以上
- Windows 10 / macOS 13 / Linux (ChromeOS Plusも可)

**セットアップ手順:**

1. `chrome://flags/#optimization-guide-on-device-model` → **Enabled**
2. `chrome://flags/#prompt-api-for-gemini-nano` → **Enabled** (または "Enabled multilingual")
3. Chrome を再起動
4. `chrome://on-device-internals` を開き、Gemini Nano モデルのダウンロード状況を確認。バージョンが `0.0.0.0` の場合は "Check for update" をクリック

**DevTools で確認する方法 (F12 → Console):**

```javascript
// Chrome 138+ の場合
typeof LanguageModel          // "function" なら有効
await LanguageModel.availability()  // "available" なら即利用可能

// 旧ビルドの場合
typeof window.ai?.languageModel   // "object" なら有効
```

有効になると起動時に「Chrome 組み込み AI (Gemini Nano) 利用可能 — ダウンロード不要」と表示される。

#### transformers.js フォールバックモデルの変更例

Chrome AI が使えない環境向けに `appsettings.json` で変更可能:

```json
{
  "LlmModelId": "onnx-community/Qwen2.5-1.5B-Instruct",
  "LlmDtype": "q4"
}
```

| モデル | サイズ (q4) | 特徴 |
|---|---|---|
| `onnx-community/Qwen3.5-0.8B-ONNX-OPT` | ~500 MB | 高性能・最新 (デフォルト) |
| `onnx-community/Qwen2.5-0.5B-Instruct` | ~350 MB | 軽量・高速 |
| `onnx-community/Qwen2.5-1.5B-Instruct` | ~900 MB | 日本語品質が向上 |

### 前提条件

- Node.js 18+ (`npm install` が csproj ビルド時に自動実行される)
- インジェストで生成した `manifest.json` と `.lbdb` ファイル
- インターネット接続 (初回: モデルダウンロード。2 回目以降は不要)

### 開発環境での起動手順

**1. インジェストで DB を生成**

```bash
dotnet run --project src/AzureMoe.Chat.Ingest -- --NoLlm --SkipR2
```

**2. 生成物を Web プロジェクトの data ディレクトリにコピー**

```bash
# Windows
copy out\manifest.json src\AzureMoe.Chat.Web\wwwroot\data\
copy out\*.lbdb         src\AzureMoe.Chat.Web\wwwroot\data\

# Linux / macOS
cp out/manifest.json src/AzureMoe.Chat.Web/wwwroot/data/
cp out/*.lbdb         src/AzureMoe.Chat.Web/wwwroot/data/
```

**3. 開発サーバーを起動**

```bash
dotnet run --project src/AzureMoe.Chat.Web
```

ブラウザで `https://localhost:5001` を開く。

### appsettings.json 設定項目

`src/AzureMoe.Chat.Web/wwwroot/appsettings.json` で変更できる。

| キー | デフォルト | 説明 |
|---|---|---|
| `ManifestUrl` | `data/manifest.json` | manifest.json の URL |
| `DbBaseUrl` | `data/` | DB ファイルのベース URL (manifest の `databaseFile` を結合) |
| `LlmModelId` | `onnx-community/Qwen2.5-0.5B-Instruct` | LLM モデル ID (HuggingFace) |
| `LlmDtype` | `q4` | 量子化精度。`q4` / `q8` / `fp16` など |
| `LlmMaxNewTokens` | `1024` | 1 回の生成の最大トークン数 |
| `EmbeddingModelId` | `Xenova/multilingual-e5-small` | 埋め込みモデル ID (HuggingFace) |
| `RagTopK` | `5` | ベクトル検索で取得するチャンク数 |
| `MaxContextChars` | `6000` | LLM に渡す文脈の最大文字数 |
| `SystemPrompt` | (組み込み) | システムプロンプト |

### UI コマンド

チャット入力欄で以下のコマンドが使える。

| コマンド | 動作 |
|---|---|
| `/help` | コマンド一覧を表示 |
| `/model` | 現在のモデル情報を表示 |
| `/history` | 会話履歴の件数を表示 |
| `/clear` | 画面と会話履歴をクリア |
| `/reload` | アプリを再起動 |
| (それ以外) | RAG クエリとして実行 |

### 動作フロー

1. 起動時: manifest.json 取得 → DB ダウンロード → LadybugDB WASM 初期化 → transformers.js モデル読み込み
2. 質問入力時: クエリ埋め込み → ベクトル検索 → コンテキスト構築 → WebLLM でストリーミング生成
3. WebGPU 非対応環境: LLM 生成をスキップし、検索結果のみ表示


