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

> **グラフ探索を使うには `--NoLlm` を付けずに実行する。**
> `--NoLlm` だと `Entity` / `AzureService` ノードと `MENTIONS` / `RELATED_TO` /
> `COVERS_SERVICE` エッジが作られず、グラフは `Post` / `Chunk` / `Tag` のみになる。
> チャットアプリの**エンティティ/サービスを辿った関連付け**にはこれらが必要なので、
> 本番の DB は OpenAI 互換 LLM を立てて (`--NoLlm` なしで) ビルドすること。
> なお埋め込みは記事タイトルを前置して生成され、各 `Chunk` には所属 `Post` の
> 日付・タイトル・年・月が非正規化保存される (チャンク単位の日付フィルタ用)。

### 診断・検証サブコマンド (`inspect`)

構築済み `.lbdb` を**読み取り専用**で開き、データが期待通りかを検証する。
「2026年2月の話題」のような時期クエリで的外れな結果が返る場合の原因切り分けに使う。

```bash
# 1. 統計サマリ: ノード/エッジ件数・日付分布(月別)・次数上位のエンティティ/サービス・サンプル Chunk
dotnet run --project src/AzureMoe.Chat.Ingest -- inspect

# DB を明示指定 (省略時は out/ → wwwroot/data/ の順に最新 .lbdb を自動検出)
dotnet run --project src/AzureMoe.Chat.Ingest -- inspect out/blog-20260614.lbdb

# 2. 任意の Cypher を実行 (結果をテーブル表示)
dotnet run --project src/AzureMoe.Chat.Ingest -- inspect --cypher \
  "MATCH (p:Post) WHERE p.date >= '2026-02-01' AND p.date < '2026-03-01' RETURN count(p)"

# 3. 自然文クエリでサンプルベクトル検索 (埋め込みモデルが必要・期待した記事が返るか確認)
dotnet run --project src/AzureMoe.Chat.Ingest -- inspect --query "2026年2月のAzure Functionsの更新" --topk 8
```

| 引数 | デフォルト | 説明 |
|---|---|---|
| (位置引数) | (自動検出) | 対象 `.lbdb` ファイルパス |
| `--cypher "..."` | — | 任意の Cypher を実行して結果を表示 |
| `--query "..."` | — | 自然文を埋め込んでベクトル検索 (上位 `--topk` 件) |
| `--model` | `model/Xenova/multilingual-e5-small` | `--query` 用 ONNX モデルのディレクトリ |
| `--topk` | `8` | `--query` で返す件数 |

> 引数なし (`inspect`) の統計表示で `Entity` / `AzureService` が **0 件**なら、その DB は
> `--NoLlm` でビルドされている。グラフ探索を使いたい場合は再ビルドが必要。

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
| LLM (テキスト生成) | transformers.js | `onnx-community/Qwen2.5-0.5B-Instruct` (q4) | Chrome AI 非対応時に自動ダウンロード |
| LLM (テキスト生成) | OpenAI 互換 HTTP | 任意 (LM Studio / Ollama 等) | `/llm` コマンドで実行中に切替可 |
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
| `onnx-community/Qwen2.5-0.5B-Instruct` | ~350 MB | 軽量・高速 (デフォルト) |
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
| `LlmMaxNewTokens` | `4096` | 最終回答の最大生成トークン数 (上限。EOS で自然停止) |
| `LlmEvalMaxTokens` | `512` | 充足判定 (Deep) の最大トークン数 |
| `EmbeddingModelId` | `Xenova/multilingual-e5-small` | 埋め込みモデル ID (HuggingFace) |
| `RetrievalMode` | `Normal` | 探索の深さ。`Fast` / `Normal` / `Deep` (UI の `/mode` でも変更可) |
| `RagTopK` | `6` | HTTP LLM モード時の最終参照数上限 |
| `MaxContextChars` | `6000` | HTTP LLM モード時に LLM へ渡す文脈の最大文字数 |
| `LocalRagTopK` | `3` | ローカル WASM LLM モード時の参照数上限 (2B 級モデル向け圧縮設定) |
| `LocalMaxContextChars` | `2500` | ローカル WASM LLM モード時の文脈最大文字数 |
| `LocalPerRefMaxChars` | `800` | ローカル WASM LLM モード時の参照1件あたりの最大文字数 |
| `HistoryTurns` | `3` | Deep モードで LLM に渡す直近会話ターン数 |
| `DeepMaxRounds` | `3` | Deep モードの検索クエリ数 (round-0 ＋ 上位タイトルでの追検索) |
| `VerifyGrounding` | `true` | 生成後に「回答が参考情報で裏付けられるか」を検査し、不十分なら警告を表示 |
| `SystemPrompt` | (組み込み) | システムプロンプト (キャラ付け + GraphRAG ルール) |

#### 探索モード (`RetrievalMode`)

検索の深さと応答速度のトレードオフを選べる。UI の `/mode` で実行中にも切替可能。

| モード | 検索の広さ | 内容 | 会話履歴 | 体感 |
|---|---|---|---|---|
| `Fast` | 狭 | グラフ探索なし・純ベクトル検索 | 使用しない | 最軽量 |
| `Normal` | 中 | グラフ探索あり | 使用しない (単発質問) | バランス (既定) |
| `Deep` | 広 | 関連エンティティまで辿る + 上位記事タイトルで決定的に追検索 (複数クエリ, 最大 `DeepMaxRounds`) | 直近 `HistoryTurns` ターン | 高精度・低速 |

**全モード共通の2段構え**:
1. *recall* — モードに応じてベクトル＋グラフ＋日付で候補を広く集める。
2. *precision* — 候補を**「元の質問」へのコサイン類似度で再ランクし、質問の関連プール (上位) から外れたものを足切り**する。
   グラフのつながり (共有タグ/エンティティ/サービス) は同点時の微加点に留め、的外れな拡張が本筋の記事を上回らないようにする。
   これにより Deep でも「件数は多いが質問に合わない」を防ぐ。

質問から日付 (例: 「2026年2月」「先月」) を検出した場合は、再ランクも日付窓の中で行い**範囲外の記事を除外**する。

> **応答時間について**: ブラウザ内 CPU では最終回答の生成時間が支配的で、これは全モード共通。
> そのため Fast と Normal の体感差は小さく (主に検索の広さと精度で差が出る)、
> 複数クエリを投げる **Deep が明確に遅い**。各ステップの進行状況はチャット画面に表示される。

**接地 (グラウンディング) の担保**: LLM が学習知識で一般論を返さないよう、全モードで次を行う。
- 関連する参考情報が1件も見つからない場合は生成を行わず、「見つからなかった」旨を返す (`VerifyGrounding` とは独立に常時)。
- 生成後に `VerifyGrounding`=true なら、回答が参考情報で裏付けられるかを検査する。裏付け不足と判定された場合は、**より厳密なプロンプト（参考情報のみ・[n] 引用必須）で1回だけ再生成**し、それでも裏付けられなければ回答の下に⚠警告を表示する。
- 検索結果は「元の質問」への関連度で再ランク・足切りされるため、文脈には質問に合致した参照のみが渡る（precision 重視）。

### UI コマンド

チャット入力欄で以下のコマンドが使える。

| コマンド | 動作 |
|---|---|
| `/help` | コマンド一覧を表示 |
| `/mode [fast\|normal\|deep]` | 探索モードを表示 / 変更 (引数なしで現在値を表示) |
| `/llm [endpoint [model]]` | 外部 OpenAI 互換 LLM を設定 (例: `/llm http://localhost:1234/v1 gpt-model`)。引数なしでローカル WASM に戻す |
| `/debug [on\|off]` | デバッグ出力の表示切替。ON 時は埋め込み・Cypher・LLM プロンプト/応答をチャット内に表示 |
| `/info` | 現在の LLM / 埋め込みモデル情報と探索モードを表示 |
| `/license` | ライセンス情報を表示 |
| `/clear` | 画面と会話履歴をクリア |
| `/reload` | アプリを再起動 |
| (それ以外) | RAG クエリとして実行 |

応答生成中は入力欄の右に **「■ 停止」ボタン**が表示され、クリックすると LLM の生成を即座に中断する
(緊急停止)。途中まで生成された回答は残る。長い生成 (`LlmMaxNewTokens` が大きい) や、まれに小型モデルが
同じ文言を繰り返すループに入った場合の保険として使える (ループ自体は `no_repeat_ngram_size` で抑制済み)。

### 動作フロー

1. 起動時: manifest.json 取得 → DB ダウンロード → LadybugDB WASM 初期化 → transformers.js モデル読み込み
2. 質問入力時 (モードにより広さが変わる): クエリ解析 (日付・キーワード抽出) → ベクトル＋グラフ＋日付で候補を収集 → **元の質問への関連度で再ランク・足切り** → URL 単位でまとめてコンテキスト構築 (本文中の URL を除去して圧縮) → LLM でストリーミング生成 → 接地検査
3. WebGPU 非対応環境: WASM CPU にフォールバックして生成 (低速)

---

## デプロイ (Cloudflare Workers)

チャットアプリは **Cloudflare Workers (Static Assets)** にデプロイする。静的アセットの配信に加えて、
小さな Worker スクリプトで「HuggingFace のモデル取得プロキシ」と「SPA フォールバック」を担う。

> **重要**: 必ず **`npx wrangler deploy`** でデプロイすること。
> ダッシュボードの手動アップロードは**静的アセットのみ**で Worker スクリプト (`worker/index.js`) を反映しないため、
> 後述の `/hf` プロキシも SPA フォールバックも動かない。

### 構成ファイル (`src/AzureMoe.Chat.Web/`)

| ファイル | 役割 |
|---|---|
| `publish.bat` | デプロイ成果物 (`publish/wwwroot`) をビルドするバッチ |
| `wrangler.jsonc` | Worker + アセットのデプロイ設定 |
| `worker/index.js` | `/hf/*` を huggingface.co へ中継 / それ以外は静的アセットへ委譲 |

#### なぜ `/hf` プロキシが必要か

アプリは `coi-serviceworker.js` で **cross-origin isolation (COEP)** を有効化している
(SharedArrayBuffer = マルチスレッド WASM のため)。この状態だと transformers.js が
`huggingface.co` から**直接**モデルを取得する際に CORS でブロックされる
(`No 'Access-Control-Allow-Origin' header` エラー)。そこで Worker で `/hf/*` を**同一オリジン**として
中継し、CORS/COEP のチェック自体を回避する。`wwwroot/js/{embeddings-interop,llm-worker}.js` は本番時のみ
`env.remoteHost` / `env.remotePathTemplate` を `/hf` 経由に切り替える (localhost は HF 直結のまま)。

`wrangler.jsonc` の `not_found_handling: "single-page-application"` で、未マッチのパスは `index.html` を返し、
Blazor のクライアントルーティング (深いリンクの直接リロード) が成立する。`run_worker_first: true` は
SPA フォールバックが `/hf/*` を飲み込む前に Worker を先に通すために必要。

### 1. 成果物のビルド

`src/AzureMoe.Chat.Web/` で `publish.bat` を実行する (ダブルクリックでも可)。

```bat
cd src\AzureMoe.Chat.Web
publish.bat
```

`publish.bat` は以下を順に行う:

1. 旧 `publish/` と `obj/Release/` を削除 (AOT をクリーンに再リンク)
2. `dotnet publish -c Release` — **AOT コンパイル + トリミング** (`wasm-tools` ワークロードが必要)
3. `.br` / `.gz` を削除 — Cloudflare がエッジで圧縮するため事前圧縮ファイルは不要 (ファイル数も削減)
4. `index.html` を `404.html` にコピー (SPA フォールバックの予備)

> **`wasm-tools` の導入** (初回のみ): `dotnet workload install wasm-tools`
> AOT は実行速度を上げる代わりに転送サイズが約 2 倍になる (brotli 後 `dotnet.native.wasm` ≈ 3.8 MB)。
> サイズ優先にしたい場合は `AzureMoe.Chat.Web.csproj` の `RunAOTCompilation` / `WasmStripILAfterAOT` を外す。

> **`index.html` のフィンガープリント**: csproj は `OverrideHtmlAssetPlaceholders=true` のため、
> ブートスクリプトは `<script src="_framework/blazor.webassembly#[.{fingerprint}].js">` の
> プレースホルダー記法で書く必要がある。プレーン名 (`blazor.webassembly.js`) だと publish 後に 404 になり
> "Initializing" のまま固まる。

### 2. デプロイ

```bash
cd src/AzureMoe.Chat.Web
npx wrangler login     # 初回のみ (ブラウザ認証)
npx wrangler deploy
```

`wrangler deploy` が `wrangler.jsonc` に従って `publish/wwwroot` (静的アセット) と `worker/index.js` を
まとめてアップロードする。

### 3. 確認

```bash
# モデルプロキシが JSON を返すか (text/html ならプロキシが効いていない)
curl -sI https://<your-worker>.workers.dev/hf/Xenova/multilingual-e5-small/resolve/main/config.json
```

ブラウザでは、トップ起動 → モデルが `…/hf/…` 経由でダウンロード → 深いパスの直接リロードでアプリ表示、を確認する。

> **キャッシュに注意**: プロキシ挙動を変えた後は、transformers.js の Cache API (`transformers-cache`) や
> coi service worker が**失敗時の古いレスポンスを保持**していることがある。Ctrl+F5 では Cache Storage は消えないため、
> DevTools → **Application → Clear site data** でサイトデータを消去してから再読み込みする。


