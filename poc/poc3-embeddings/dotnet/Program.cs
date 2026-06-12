using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

// POC-3 (.NET side): embed the shared test texts with multilingual-e5-small
// via Microsoft.ML.OnnxRuntime + HuggingFace tokenizer.json, and dump the
// result for comparison against transformers.js.

var pocRoot = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
while (!File.Exists(Path.Combine(pocRoot, "texts.json")))
    pocRoot = Path.GetDirectoryName(pocRoot) ?? throw new InvalidOperationException("texts.json not found");

var modelDir = Path.Combine(pocRoot, "model", "Xenova", "multilingual-e5-small");
var texts = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(pocRoot, "texts.json")))!;

var tokenizer = new Tokenizer(Path.Combine(modelDir, "tokenizer.json"));
using var session = new InferenceSession(Path.Combine(modelDir, "onnx", "model.onnx"));
Console.WriteLine($"model inputs: {string.Join(", ", session.InputMetadata.Keys)}");

// XLM-RoBERTa special tokens: <s> = 0, </s> = 2
const long BosId = 0, EosId = 2;

var results = new List<object>();
foreach (var text in texts)
{
    var encoded = tokenizer.Encode(text).Select(id => (long)id).ToList();
    if (encoded.Count == 0 || encoded[0] != BosId) encoded.Insert(0, BosId);
    if (encoded[^1] != EosId) encoded.Add(EosId);

    var n = encoded.Count;
    var inputIds = new DenseTensor<long>(encoded.ToArray(), [1, n]);
    var attentionMask = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), [1, n]);

    var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
        NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
    };
    if (session.InputMetadata.ContainsKey("token_type_ids"))
        inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
            new DenseTensor<long>(new long[n], [1, n])));

    using var outputs = session.Run(inputs);
    var hidden = outputs.First(o => o.Name == "last_hidden_state").AsTensor<float>();
    var dim = hidden.Dimensions[2];

    // mean pooling over the (all-ones) attention mask, then L2 normalize —
    // must mirror transformers.js {pooling: 'mean', normalize: true}
    var embedding = new float[dim];
    for (var t = 0; t < n; t++)
        for (var d = 0; d < dim; d++)
            embedding[d] += hidden[0, t, d];
    for (var d = 0; d < dim; d++) embedding[d] /= n;
    var norm = MathF.Sqrt(embedding.Sum(v => v * v));
    for (var d = 0; d < dim; d++) embedding[d] /= norm;

    Console.WriteLine($"\"{text[..Math.Min(40, text.Length)]}...\" -> {n} tokens, dim {dim}");
    results.Add(new { text, tokenIds = encoded, embedding });
}

var outPath = Path.Combine(pocRoot, "embeddings-dotnet.json");
File.WriteAllText(outPath, JsonSerializer.Serialize(results));
Console.WriteLine($"Wrote {outPath}");
