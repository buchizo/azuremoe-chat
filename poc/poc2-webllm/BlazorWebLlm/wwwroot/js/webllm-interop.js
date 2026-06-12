// POC-2: thin interop layer between Blazor and WebLLM.
// Production will self-host the WebLLM bundle and model weights (R2);
// for the POC we pull both from public CDNs.
import * as webllm from "https://esm.run/@mlc-ai/web-llm";

let engine = null;

export function isWebGpuAvailable() {
  return typeof navigator !== "undefined" && !!navigator.gpu;
}

export async function initEngine(modelId, dotnetRef) {
  engine = await webllm.CreateMLCEngine(modelId, {
    initProgressCallback: (report) => {
      console.log(`[webllm] ${(report.progress * 100).toFixed(0)}% ${report.text ?? ""}`);
      dotnetRef.invokeMethodAsync("OnInitProgress", report.text ?? "", report.progress ?? 0);
    },
  });
  return true;
}

export async function chat(messagesJson, dotnetRef) {
  if (!engine) throw new Error("engine not initialized");
  const messages = JSON.parse(messagesJson);
  const stream = await engine.chat.completions.create({
    messages,
    stream: true,
    stream_options: { include_usage: true },
  });
  let full = "";
  for await (const chunk of stream) {
    const delta = chunk.choices?.[0]?.delta?.content ?? "";
    if (delta) {
      full += delta;
      await dotnetRef.invokeMethodAsync("OnToken", delta);
    }
    if (chunk.usage) {
      await dotnetRef.invokeMethodAsync("OnUsage", JSON.stringify(chunk.usage));
    }
  }
  await dotnetRef.invokeMethodAsync("OnCompleted", full);
}

// Lets the POC page publish a machine-readable result for the Playwright test.
export function publishResult(json) {
  window.__pocResult = JSON.parse(json);
}
