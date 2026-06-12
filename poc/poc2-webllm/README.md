# POC-2: Blazor WASM + WebLLM ストリーミング検証 — 結果: 成功 (2026-06-13)

Blazor WebAssembly (.NET 10) から JS interop で WebLLM (WebGPU) を起動し、
ストリーミング応答を C# 側のイベントで受け取れるかの検証。**全項目 PASS。**

## 実行方法

```powershell
cd BlazorWebLlm && dotnet publish -c Release
cd ../test && npm install
node browser-test.mjs    # headless Chromium、初回はモデル ~350MB DL (プロファイルにキャッシュ)
```

## 結果 (RTX 4060 Ti / Qwen3-0.6B-q4f16_1-MLC)

| 指標 | 値 |
|---|---|
| ストリーミング | C# に 110 token イベント着信 (`[JSInvokable]` 経由) |
| 生成速度 | decode 51.6 tok/s、TTFT 4.4s (エンジン初期化込み) |
| モデルDL | 初回 ~10s (回線依存)、Cache API にキャッシュされ2回目以降スキップ |
| 日本語 | 出力される (0.6B なので品質は粗い) |

## 実装パターン (本番にそのまま流用)

- `wwwroot/js/webllm-interop.js`: ESM。`CreateMLCEngine(modelId, {initProgressCallback})` →
  `engine.chat.completions.create({stream: true})` を for-await し、
  `dotnetRef.invokeMethodAsync("OnToken", delta)` で C# へプッシュ
- `WebLlmService.cs`: `IJSRuntime` で動的 import、`DotNetObjectReference` を渡し
  `[JSInvokable]` メソッドを C# イベントに変換

## 知見 (実装に影響)

1. **`navigator.gpu` は secure context のみ** — about:blank では undefined。localhost/https は OK
2. **headless Chromium で WebGPU を使うには `--use-angle=d3d11` が必要** (Windows)。
   実 GPU (lovelace) がヘッドレスでも使える — CI 的な自動テストが可能
3. **Qwen3 の thinking 制御**: `/no_think` ソフトスイッチでも空の `<think></think>` が出力に残る。
   本番では `extra_body: {enable_thinking: false}` (WebLLM が対応していれば) か出力の `<think>` ブロック除去が必要
4. 0.6B は日本語の内容品質が不足。計画通り **Qwen3-1.7B を標準**にするのが妥当
5. powerPreference オプションは Windows では無視される (crbug.com/369219127、無害)
