// POC-1 authoritative browser test: serves the page + wasm package + db file
// over HTTP, loads it in headless Chromium, and asserts the query results.
import http from "http";
import { readFile } from "fs/promises";
import { resolve, extname } from "path";
import { chromium } from "playwright";

const root = resolve(".");
const dbFile = resolve(process.argv[2] ?? "../builder/out/poc1.db");

const mime = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript",
  ".mjs": "text/javascript",
  ".wasm": "application/wasm",
  ".db": "application/octet-stream",
};

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, "http://localhost");
    let file;
    if (url.pathname === "/" || url.pathname === "/index.html") file = resolve(root, "public/index.html");
    else if (url.pathname === "/poc1.db") file = dbFile;
    else file = resolve(root, "." + url.pathname);
    if (!file.startsWith(root) && file !== dbFile) throw new Error("forbidden");
    const body = await readFile(file);
    res.writeHead(200, { "Content-Type": mime[extname(file)] ?? "application/octet-stream" });
    res.end(body);
  } catch {
    res.writeHead(404);
    res.end("not found");
  }
});

await new Promise((ok) => server.listen(0, ok));
const port = server.address().port;
console.log(`serving on http://localhost:${port}`);

const browser = await chromium.launch();
const page = await browser.newPage();
page.on("console", (msg) => console.log(`[browser:${msg.type()}] ${msg.text()}`));
page.on("pageerror", (err) => console.log(`[pageerror] ${err}`));

await page.goto(`http://localhost:${port}/`);
const result = await page.waitForFunction(() => window.__pocResult, null, { timeout: 120000 })
  .then((h) => h.jsonValue());

await browser.close();
server.close();

console.log("\nresult:", JSON.stringify(result, null, 2));

let failures = 0;
const check = (label, cond) => {
  console.log(`${cond ? "PASS" : "FAIL"}: ${label}`);
  if (!cond) failures++;
};

check("page reported ok", result.ok === true);
if (result.ok) {
  const topIds = result.vector.slice(0, 2).map((r) => Number(r.id)).sort((a, b) => a - b);
  check("vector index returns 4 rows", result.vector.length === 4);
  check("nearest neighbours are ids 2 and 10", topIds[0] === 2 && topIds[1] === 10);
  check("Japanese text intact", String(result.vector[0].text).includes("チャンク"));
  check("traversal: Azure=8, Cloudflare=8",
    result.traversal.length === 2 && result.traversal.every((r) => Number(r.chunks) === 8));
  check("vector+graph combined works",
    result.combined.length === 2 && result.combined.every((r) => r.entity === "Azure"));
}

console.log(failures === 0 ? "\nPOC-1 BROWSER RESULT: SUCCESS" : `\nPOC-1 BROWSER RESULT: ${failures} FAILURE(S)`);
process.exit(failures === 0 ? 0 : 1);
