// POC-2: serve the published Blazor app, load it in Chromium with WebGPU,
// and assert that WebLLM streams a Japanese answer back into C#.
// First run downloads the model (~600MB) from Hugging Face; the persistent
// browser profile caches it for subsequent runs.
import http from "http";
import { readFile, access } from "fs/promises";
import { resolve, extname, join } from "path";
import { chromium } from "playwright";

const wwwroot = resolve(process.argv[2] ?? "../BlazorWebLlm/bin/Release/net10.0/publish/wwwroot");
await access(join(wwwroot, "index.html")).catch(() => {
  console.error(`publish output not found at ${wwwroot} — run 'dotnet publish -c Release' first`);
  process.exit(2);
});

const mime = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript",
  ".mjs": "text/javascript",
  ".json": "application/json",
  ".wasm": "application/wasm",
  ".css": "text/css",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".dat": "application/octet-stream",
};

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, "http://localhost");
  let file = resolve(wwwroot, "." + url.pathname);
  try {
    if (!file.startsWith(wwwroot)) throw new Error("forbidden");
    let body;
    try {
      body = await readFile(file);
    } catch {
      file = join(wwwroot, "index.html"); // SPA fallback
      body = await readFile(file);
    }
    res.writeHead(200, { "Content-Type": mime[extname(file)] ?? "application/octet-stream" });
    res.end(body);
  } catch {
    res.writeHead(404);
    res.end();
  }
});
await new Promise((ok) => server.listen(0, ok));
const port = server.address().port;
console.log(`serving ${wwwroot} on http://localhost:${port}`);

const profileDir = resolve("./.chrome-profile"); // persists the model cache between runs

// --use-angle=d3d11 is required for headless Chromium to expose the real GPU
// adapter on Windows. navigator.gpu only exists in secure contexts, so the
// WebGPU check happens inside the app page (http://localhost is secure).
const context = await chromium.launchPersistentContext(profileDir, {
  headless: true,
  channel: "chromium",
  args: ["--enable-unsafe-webgpu", "--use-angle=d3d11"],
});
const page = context.pages()[0] ?? await context.newPage();

let lastLog = 0;
page.on("console", (msg) => {
  const text = msg.text();
  // throttle the very chatty fetch progress lines
  if (text.startsWith("[webllm]") && Date.now() - lastLog < 2000) return;
  lastLog = Date.now();
  console.log(`[browser] ${text}`);
});
page.on("pageerror", (err) => console.log(`[pageerror] ${err}`));

await page.goto(`http://localhost:${port}/`);
const result = await page.waitForFunction(() => window.__pocResult, null, { timeout: 1200000 })
  .then((h) => h.jsonValue());

await context.close();
server.close();

console.log("\nresult:", JSON.stringify(result, null, 2));

let failures = 0;
const check = (label, cond) => {
  console.log(`${cond ? "PASS" : "FAIL"}: ${label}`);
  if (!cond) failures++;
};

check("page reported ok", result.ok === true);
if (result.ok) {
  check("answer is non-empty", typeof result.answer === "string" && result.answer.trim().length > 0);
  check("streamed in multiple token events", result.tokenEvents > 5);
  check("usage reported", !!result.usage);
  console.log(`\nanswer:\n${result.answer}`);
}

console.log(failures === 0 ? "\nPOC-2 RESULT: SUCCESS" : `\nPOC-2 RESULT: ${failures} FAILURE(S)`);
process.exit(failures === 0 ? 0 : 1);
