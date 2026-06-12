// POC-3 (JS side): embed the same texts with transformers.js using the SAME
// local model files the .NET side used, then compare token ids and cosine
// similarity against embeddings-dotnet.json.
import { env, pipeline, AutoTokenizer } from "@huggingface/transformers";
import { readFile } from "fs/promises";
import { resolve } from "path";

env.localModelPath = resolve("../model");
env.allowRemoteModels = false;

const texts = JSON.parse(await readFile(resolve("../texts.json"), "utf8"));
const dotnet = JSON.parse(await readFile(resolve("../embeddings-dotnet.json"), "utf8"));

const tokenizer = await AutoTokenizer.from_pretrained("Xenova/multilingual-e5-small");
const extractor = await pipeline("feature-extraction", "Xenova/multilingual-e5-small", { dtype: "fp32" });

const cosine = (a, b) => {
  let dot = 0, na = 0, nb = 0;
  for (let i = 0; i < a.length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
  return dot / Math.sqrt(na * nb);
};

let failures = 0;
const check = (label, cond) => {
  console.log(`${cond ? "PASS" : "FAIL"}: ${label}`);
  if (!cond) failures++;
};

for (let i = 0; i < texts.length; i++) {
  const text = texts[i];
  const ref = dotnet.find((r) => r.text === text);
  if (!ref) { check(`dotnet result exists for text ${i}`, false); continue; }

  const { input_ids } = await tokenizer(text);
  const jsIds = Array.from(input_ids.data, Number);
  const idsMatch = JSON.stringify(jsIds) === JSON.stringify(ref.tokenIds.map(Number));

  const output = await extractor(text, { pooling: "mean", normalize: true });
  const jsEmb = Array.from(output.data);
  const sim = cosine(jsEmb, ref.embedding);

  console.log(`text ${i}: tokens js=${jsIds.length} dotnet=${ref.tokenIds.length}, cosine=${sim.toFixed(8)}`);
  if (!idsMatch) {
    console.log(`  js ids:     ${JSON.stringify(jsIds.slice(0, 12))}...`);
    console.log(`  dotnet ids: ${JSON.stringify(ref.tokenIds.slice(0, 12))}...`);
  }
  check(`text ${i}: token ids identical`, idsMatch);
  check(`text ${i}: cosine > 0.9999`, sim > 0.9999);
}

console.log(failures === 0 ? "\nPOC-3 RESULT: SUCCESS" : `\nPOC-3 RESULT: ${failures} FAILURE(S)`);
process.exit(failures === 0 ? 0 : 1);
