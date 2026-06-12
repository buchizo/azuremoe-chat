# POC-1: Ladybug DB ファイル可搬性検証 — 結果: 成功 (2026-06-13)

デスクトップ (ladybug-dotnet, native) で構築した vector index 込みの DB ファイルを、
ブラウザの `@ladybugdb/wasm-core` で開けるかの検証。**全項目 PASS。**

## 実行方法

```powershell
cd builder && dotnet run            # out/poc1.db を生成 + ネイティブ側で検証
cd ../webtest && npm install
node node-test-nodefs.mjs           # Node.js 変種でのフォーマット互換スモーク
node browser-test.mjs               # 本命: headless Chromium での実ブラウザ検証
```

## 確認できたこと

| 項目 | 結果 |
|---|---|
| フォーマット互換 | native 0.17.0 で構築 → wasm 0.17.0 / 0.17.1 で開ける (storage version 41) |
| vector index | デスクトップで `CREATE_VECTOR_INDEX` したものがブラウザの `QUERY_VECTOR_INDEX` でそのまま動く (再構築不要) |
| グラフ走査 | REL テーブル (MENTIONS) の MATCH が動く |
| 複合 (GraphRAGコア) | `QUERY_VECTOR_INDEX → WITH node MATCH (node)-[:MENTIONS]->(e)` のベクトル検索→グラフ展開が動く |
| 日本語 | STRING プロパティの日本語が欠損なし |

## 重要な知見 (実装に影響)

1. **wasm では `INSTALL`/`LOAD vector` は不要かつ不可** — 拡張は静的リンク済みで、
   `LOAD vector` は「Extensions are not available in the WASM environment」エラーになる。
   そのまま `QUERY_VECTOR_INDEX` を呼べば動く。取り込みツール (native) 側のみ INSTALL/LOAD が必要。
2. **0.17.x の async (Worker) ラッパーはブラウザでファイル注入が壊れている** —
   ブラウザビルドは WasmFS で、JS から見える FS API は `createDataFile`/`createPath`/
   `createPreloadedFile`/`cwd` のみ。ラッパーの `FS.writeFile`/`mkdir` は内部で存在しない
   メソッドを呼んで失敗する (0.17.0/0.17.1 で確認)。公式 examples (mountOpfs 等) は未リリースの
   新版前提。**→ 本番は sync ビルドを自前の Web Worker でラップし、`createDataFile` で注入する。**
3. **バージョンペアリング**: NuGet `LadybugDB` は `0.17.0-alpha.1` (engine 0.17.0) のみ公開。
   npm は 0.17.0 / 0.17.1。0.17.0 構築ファイル → 0.17.1 エンジンは問題なし (同一 storage format)。
4. DB は単一ファイル (このサンプルで 276KB)。`FLOAT[8]` の cosine 距離はネイティブと
   ブラウザで一致 (浮動小数の丸め差のみ)。

## ファイル構成

- `builder/` — .NET 10 console。`LadybugDB` + `LadybugDB.Native.win-x64` で DB 構築
- `webtest/node-test-nodefs.mjs` — Node.js 変種 (NODEFS) でのスモークテスト
- `webtest/browser-test.mjs` + `public/index.html` — Playwright + Chromium での実ブラウザ検証
- `webtest/public/diag.html` + `diag-test.mjs` — WasmFS の FS API 診断 (知見2の根拠)
