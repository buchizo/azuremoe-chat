# Third-Party Notices / サードパーティ ライセンス

本アプリ (AzureMoe Chat) は以下のモデル・ライブラリを利用しています。各コンポーネントの
著作権はそれぞれの権利者に帰属します。再配布の際は本ファイルおよび各ライセンス全文を
同梱してください。

---

## LLM モデル

### LiquidAI/LFM2.5-1.2B-JP-ONNX

- **ライセンス:** LFM Open License v1.0 (`lfm1.0`)
- **権利者:** Liquid AI, Inc.
- **ライセンス全文:** https://huggingface.co/LiquidAI/LFM2.5-1.2B-JP-202606/blob/main/LICENSE
- **モデル:** https://huggingface.co/LiquidAI/LFM2.5-1.2B-JP-ONNX

> ⚠ **重要 (再配布時の義務):** LFM Open License v1.0 は、モデル（およびその派生物）を
> 配布する際に **LICENSE ファイル本体の同梱**を要求します。本アプリはモデルを同梱せず
> 実行時に HuggingFace から直接ダウンロードしますが、モデルウェイトを再配布する場合は
> 上記 LICENSE 全文ファイルを必ず添付してください。商用利用・収益閾値 ($10,000,000) 等の
> 条件があるため、利用前に全文をご確認ください。

---

## 埋め込みモデル

### Xenova/multilingual-e5-small

- **ライセンス:** MIT License
- **元モデル:** intfloat/multilingual-e5-small (MIT)
- **リンク:** https://huggingface.co/intfloat/multilingual-e5-small

---

## JavaScript ライブラリ

| パッケージ | バージョン | ライセンス | リンク |
|---|---|---|---|
| @huggingface/transformers (transformers.js) | 4.2.0 | Apache-2.0 | https://github.com/huggingface/transformers.js |
| onnxruntime-web (transformers.js 同梱) | — | MIT | https://github.com/microsoft/onnxruntime |
| @ladybugdb/wasm-core | 0.17.1 | MIT | https://ladybugdb.com/ |

---

## NuGet パッケージ

| パッケージ | バージョン | ライセンス | リンク |
|---|---|---|---|
| Markdig | 0.37.0 | BSD-2-Clause | https://github.com/xoofx/markdig |
| Microsoft.AspNetCore.Components.WebAssembly | 10.0.9 | MIT | https://github.com/dotnet/aspnetcore |
| Microsoft.AspNetCore.Components.WebAssembly.DevServer | 10.0.9 | MIT (開発時のみ・非再配布) | https://github.com/dotnet/aspnetcore |

---

アプリ内では `/license` コマンドでこの一覧の要約を表示できます。
