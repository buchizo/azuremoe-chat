import http from "http";
import { readFile } from "fs/promises";
import { resolve, extname } from "path";
import { chromium } from "playwright";

const root = resolve(".");
const mime = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript",
  ".wasm": "application/wasm",
};

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, "http://localhost");
    const file = url.pathname === "/" ? resolve(root, "public/diag.html") : resolve(root, "." + url.pathname);
    const body = await readFile(file);
    res.writeHead(200, { "Content-Type": mime[extname(file)] ?? "application/octet-stream" });
    res.end(body);
  } catch {
    res.writeHead(404);
    res.end();
  }
});
await new Promise((ok) => server.listen(0, ok));
const port = server.address().port;

const browser = await chromium.launch();
const page = await browser.newPage();
page.on("pageerror", (err) => console.log(`[pageerror] ${err}`));
await page.goto(`http://localhost:${port}/public/diag.html`);
const result = await page.waitForFunction(() => window.__pocResult, null, { timeout: 120000 })
  .then((h) => h.jsonValue());
await browser.close();
server.close();
console.log(JSON.stringify(result, null, 2));
