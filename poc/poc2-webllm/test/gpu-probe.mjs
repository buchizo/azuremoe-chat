// Probe which Chromium channel/flag combination exposes a WebGPU adapter.
import { chromium } from "playwright";
import { resolve } from "path";
import { rm } from "fs/promises";

const combos = [
  { name: "bundled headless", channel: undefined, headless: true, args: ["--enable-unsafe-webgpu"] },
  { name: "bundled headless +angle", channel: undefined, headless: true, args: ["--enable-unsafe-webgpu", "--use-angle=d3d11"] },
  { name: "chromium-channel headless +angle", channel: "chromium", headless: true, args: ["--enable-unsafe-webgpu", "--use-angle=d3d11"] },
  { name: "msedge headless +angle", channel: "msedge", headless: true, args: ["--enable-unsafe-webgpu", "--use-angle=d3d11"] },
  { name: "msedge headed", channel: "msedge", headless: false, args: ["--enable-unsafe-webgpu"] },
  { name: "chromium-channel headed", channel: "chromium", headless: false, args: ["--enable-unsafe-webgpu"] },
];

for (const combo of combos) {
  const profile = resolve(`./.probe-profile`);
  await rm(profile, { recursive: true, force: true });
  try {
    const ctx = await chromium.launchPersistentContext(profile, {
      headless: combo.headless,
      channel: combo.channel,
      args: combo.args,
    });
    const page = ctx.pages()[0] ?? await ctx.newPage();
    await page.goto("https://example.com"); // navigator.gpu requires a secure context
    const info = await page.evaluate(async () => {
      if (!navigator.gpu) return { gpu: false };
      const adapter = await navigator.gpu.requestAdapter().catch((e) => null);
      if (!adapter) return { gpu: true, adapter: null };
      const ai = adapter.info ?? {};
      return { gpu: true, adapter: { vendor: ai.vendor, architecture: ai.architecture, device: ai.device, description: ai.description } };
    });
    console.log(`${combo.name}: ${JSON.stringify(info)}`);
    await ctx.close();
  } catch (e) {
    console.log(`${combo.name}: LAUNCH FAILED ${String(e).split("\n")[0]}`);
  }
}
