# Blog GraphRAG Chat (azuremoe-chat) — アーキテクチャ計画

## Context

WordPress上の既存ブログ記事を知識ソースとして会話できるチャットアプリを新規開発する。
特徴は「サーバーレス・フルクライアントサイド」: Cloudflare Pages に静的配置した
Blazor WebAssembly アプリが、ブラウザ内で Graph DB (GraphRAG) とローカルLLMを動かし、
チャット時には外部APIに一切依存しない。データ更新は別途インジェストツール(ローカル手動実行)で行う。

### 確定要件 (ユーザー確認済み)
- フロントエンド: Blazor WASM (.NET/C#)、Console風UI、Cloudflare Pages
- 実行環境: **幅広い環境対応** — WebGPUがあればGPU推論、なければCPUフォールバック
- 取り込み側: クラウドLLM API (Claude API) 利用OK、当面ローカル手動実行
- 言語: 日本語中心

## Phase 0 POC 結果 (2026-06-13) — 3検証すべて成功

| POC | 結果 | 詳細 |
|---|---|---|
| POC-1: DB可搬性 | **成功** | native (ladybug-dotnet 0.17.0) 構築の vector index 込み DB を Chromium 上の wasm エンジンで開き、vector検索・グラフ走査・複合クエリが動作。`poc/poc1-db-portability/README.md` |
| POC-2: WebLLM連携 | **成功** | Blazor → JS interop → WebLLM (WebGPU) で Qwen3-0.6B が動作。C# に 110 ストリーミングイベント、51.6 tok/s (RTX 4060 Ti)。`poc/poc2-webllm/README.md` |
| POC-3: 埋め込み一致 | **成功** | OnnxRuntime(.NET) と transformers.js で multilingual-e5-small のトークンID完全一致・コサイン類似度 1.0。`poc/poc3-embeddings/README.md` |

POC で得た設計上の重要知見:
- **wasm では拡張の INSTALL/LOAD は不要かつ不可** (vector は静的リンク済み)。native 側のみ INSTALL/LOAD する
- **0.17.x の wasm-core async ラッパーはブラウザでのファイル注入が壊れている** → 本番は sync ビルドを自前 Web Worker でラップし `FS.createDataFile` で DB を注入
- バージョンペアリング: NuGet `LadybugDB 0.17.0-alpha.1` (engine 0.17.0) ↔ npm `@ladybugdb/wasm-core 0.17.x` (storage format 41 で互換確認済み)
- 埋め込みは両側で同一 `tokenizer.json` + 同一 ONNX を使えば完全一致 (.NET 側は `Tokenizers.DotNet`)
- Qwen3 は `<think>` ブロックの除去/無効化処理が必要。0.6B は品質不足、計画通り 1.7B を標準に

## 実現可否の調査結果 (2026-06 時点) — 結論: 実現可能

| 論点 | 結果 |
|---|---|
| Ladybug (Kuzu後継) | 健在。v0.17.1、月次リリース。公式WASMビルド `@ladybugdb/wasm-core` あり。vector/FTS拡張同梱。Kuzu時代に「フルブラウザ内Graph RAG」公式デモ実績あり |
| ladybug-dotnet | P/Invoke バインディング。**browser-wasm 非対応 → Blazor WASM 内では動かない**。前例も皆無 |
| ブラウザ内LLM | WebLLM (WebGPU必須) が本命。wllama (llama.cpp wasm) がCPUフォールバック。**純.NETでのブラウザ内推論は不可能** (LLamaSharp/ONNX Runtime .NET は browser-wasm 非対応) |
| Blazor WASM | .NET 10 で安定。実質シングルスレッド、ヒープ上限 ~2GB (wasm32)。JSImport/JSExport で高速 interop |
| Cloudflare Pages | Blazor 公式ガイドあり。**25MiB/ファイル制限** → 大容量アセット(モデル・DB)は R2 へ |

### 方式変更点 (当初想定からの差分)
**ladybug-dotnet はチャットアプリ側では使えない。** Graph DB・LLM・埋め込みはいずれも
JS の wasm ライブラリを **Blazor の JS interop (JSImport/JSExport)** で呼ぶ構成にする。
C#/Blazor は「UI + RAGパイプラインのオーケストレーション」を担当。
ladybug-dotnet は**インジェストツール側 (デスクトップ native)** で使う。

## アーキテクチャ

```
[WordPress] --REST API--> ┌─ インジェストツール (.NET 10 console, ローカル実行) ─┐
                          │ 1. WP REST API で投稿取得 (差分取り込み)              │
                          │ 2. HTML→テキスト化、チャンク分割                      │
                          │ 3. Claude API: エンティティ・関係抽出                 │
                          │ 4. 埋め込み生成: multilingual-e5-small (ONNX, local)  │
                          │ 5. ladybug-dotnet で Graph DB 構築 + vector index     │
                          │ 6. DBファイル + manifest.json を R2 へアップロード     │
                          └──────────────────────────────────────────────┘
                                              v
                  [Cloudflare R2]  DBファイル / LLMモデル / wasm libs (CORS設定)
                                              ^ 動的ロード + ブラウザ内キャッシュ
┌─ ブラウザ (Cloudflare Pages 配信) ──────────────────────────────────┐
│ Blazor WASM (.NET 10 standalone)                                       │
│  ├─ Console風UI: XtermBlazor (xterm.js)                                │
│  ├─ RAGパイプライン (C#): クエリ埋め込み→vector検索→グラフ展開→生成    │
│  └─ JS interop 層 (JSImport/JSExport)                                  │
│      ├─ @ladybugdb/wasm-core (async/Worker版): Cypher実行              │
│      │    DBファイルは R2→fetch→FS.writeFile、IDBFSでキャッシュ        │
│      ├─ WebLLM (WebGPU) / wllama (CPUフォールバック): 生成・streaming  │
│      │    モデルは R2 から custom AppConfig で配信、Cache APIキャッシュ │
│      └─ transformers.js: クエリ埋め込み (multilingual-e5-small)        │
└────────────────────────────────────────────────────────────────┘
```

## 主要な技術選定

| 項目 | 選定 | 理由 |
|---|---|---|
| Graph DB (ブラウザ) | `@ladybugdb/wasm-core` async版 (Worker実行、シングルスレッドビルド) | COOP/COEP不要でR2連携が単純。性能不足なら後からマルチスレッド版へ |
| Graph DB (取り込み) | `LadybugDB` NuGet (ladybug-dotnet) | **バージョンを wasm-core と厳密一致させる** (v0.17.0でフォーマット変更があったため必須) |
| LLM (GPU) | WebLLM + Qwen3-1.7B (標準) / Qwen3-4B (高スペック向け選択肢) | 日本語性能と公式プリビルトの両立。gemma-2-2b-jpn-it も候補 |
| LLM (CPU) | wllama + Qwen3-0.6B〜1.7B GGUF (シングルスレッドで開始) | WebGPU非対応環境向け。遅いことをUI上で明示 |
| 埋め込み | multilingual-e5-small (~120MB) — ブラウザ: transformers.js / 取り込み: Microsoft.ML.OnnxRuntime | **両側で完全に同一モデル**を使い埋め込み空間を一致させる |
| エンティティ抽出 | Claude API (claude-sonnet-4-6 か当時の最新) | GraphRAGの品質はここで決まる。日本語の固有表現・関係抽出 |
| UI | XtermBlazor v2.x | xterm.js ラッパー、活発にメンテ中 |
| AOT | 当面オフ (IL interpreter + trimming) | 25MiB制限回避とビルド単純化。性能課題が出たら再検討 |
| COOP/COEP | 当面設定しない | SharedArrayBuffer不要構成。CPU推論が遅すぎる場合のみ `_headers` で有効化し、R2側にCORP/CORS追加 |

### グラフスキーマ (初期案)
- ノード: `Post {id, title, url, date}` / `Chunk {text, embedding[384]}` / `Entity {name, type}` / `Tag`
- エッジ: `Post-HAS_CHUNK->Chunk` / `Chunk-MENTIONS->Entity` / `Entity-RELATED_TO->Entity {description}` / `Post-TAGGED->Tag`
- 検索フロー: クエリ埋め込み → Chunk vector検索 (top-k) → MENTIONS/RELATED_TO を1〜2ホップ展開 → 関連Post/Entity情報を文脈に追加 → 出典(Post url/title)付きでLLMに渡す

## リポジトリ構成

```
azuremoe-chat/
  src/
    AzureMoe.Chat.Web/      # Blazor WASM standalone (.NET 10)
      wwwroot/js/           # interop モジュール (ladybug.js, llm.js, embeddings.js)
    AzureMoe.Chat.Ingest/   # インジェストCLI (.NET 10 console)
    AzureMoe.Chat.Core/     # 共有: グラフスキーマ定数, manifest型, チャンク化ロジック
  poc/                      # Phase 0 の検証コード
  docs/architecture.md      # 本計画の要約を転記
```

## 開発フェーズ

### Phase 0: リスク検証 POC (最初にやる — ここで失敗したら方式転換)
1. **DBファイル可搬性**: ladybug-dotnet (native) で vector index 込みの小さなDBを構築 → ブラウザの `@ladybugdb/wasm-core` で開いて vector検索が動くか確認。
   ※ 唯一の「未確認かつ代替コストが高い」リスク。失敗時の代替: ブラウザ側でCSV/Parquetからロード+index構築、または埋め込み検索のみJS実装
2. **Blazor + WebLLM**: Telerik の記事パターン (JS module + DotNetObjectReference streaming callback) で Qwen3-1.7B のストリーミング応答を確認
3. **埋め込み一致**: 同一テキストを OnnxRuntime(.NET) と transformers.js で埋め込み、コサイン類似度 ≈ 1.0 を確認

### Phase 1: インジェストツール
- WordPress REST API クライアント (`/wp-json/wp/v2/posts`、ページング、modified日時での差分取得)
- HTMLクリーニング → チャンク分割 (見出し単位 + サイズ上限)
- Claude API でエンティティ・関係抽出 (structured output / tool use で JSON スキーマ固定)
- e5-small 埋め込み生成 → Ladybug DB 構築 → vector index 作成
- manifest.json (DBバージョン、エンジンバージョン、件数、更新日時) と共に R2 アップロード (S3互換API)

### Phase 2: チャットアプリ
- Blazor WASM プロジェクト + XtermBlazor の Console UI (起動シーケンス演出、コマンド体系: `/help` `/model` `/reload` 等)
- JS interop 層 3モジュール (ladybug / llm / embeddings) — C# 側は `IGraphStore` `ILlmEngine` `IEmbedder` 抽象に
- アセットローダー: R2 から DB/モデルを進捗表示付きダウンロード、IDBFS/Cache API キャッシュ、manifest による更新検知
- RAGパイプライン実装 (上記検索フロー)、出典表示
- WebGPU 検出 (`navigator.gpu`) → WebLLM / wllama 自動選択

### Phase 3: デプロイ・仕上げ
- Cloudflare Pages デプロイ (ビルドスクリプト、25MiB制限の確認、必要なら `loadBootResource` + .br)
- R2 の CORS 設定、キャッシュ戦略 (immutable + manifest でバージョニング)
- 低スペック環境のフォールバック動作確認、エラーUX

## 検証方法
- Phase 0: poc/ 配下の各検証が成功すること (特にPOC-1のブラウザ内 vector検索)
- Phase 1: 実ブログに対して実行し、Ladybug Explorer 等でグラフを目視確認。再実行で差分のみ更新されること
- Phase 2: `dotnet run` でローカル起動 → 初回ロード(ダウンロード)→ 質問に出典付き日本語回答が返ること。2回目以降のロードはキャッシュから高速起動、機内モード(オフライン)でも会話できること
- Phase 3: Pages のプレビューデプロイで WebGPU あり/なし両環境の実機確認

## 既知のリスクと割り切り
- **DBサイズ予算**: ladybug-wasm は独自 wasm モジュール(独自ヒープ)だが wasm32 のため実質 1〜1.5GB が上限。ブログ規模なら余裕の見込み
- **CPUフォールバックの速度**: シングルスレッド wllama は数 tok/s。体験としては「動くが遅い」。許容できなければ COOP/COEP 有効化で改善
- **バージョンロックイン**: Ladybug のエンジンバージョンを取り込み側/ブラウザ側で常に同時更新する運用が必要 (Core プロジェクトで一元管理)
- **WebLLM の Blazor 用 NuGet は存在しない**: interop 層は自作 (薄い。Telerik 記事が雛形)
