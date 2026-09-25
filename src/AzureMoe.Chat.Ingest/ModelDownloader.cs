using AzureMoe.Chat.Core;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Fetches the files <see cref="OnnxEmbedder"/> needs from HuggingFace at the
/// profile's pinned revision — the same commit the browser requests — so both
/// sides are guaranteed to load byte-identical tokenizer and ONNX files.
/// </summary>
public static class ModelDownloader
{
    public static async Task EnsureAsync(
        string modelDir, EmbeddingProfile profile, string dtype, Action<string> log, CancellationToken ct)
    {
        string[] files = ["tokenizer.json", "tokenizer_config.json", "config.json", $"onnx/{EmbeddingProfile.OnnxFileName(dtype)}"];
        var missing = files.Where(f => !File.Exists(Path.Combine(modelDir, f))).ToList();
        if (missing.Count == 0) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        foreach (var file in missing)
        {
            var url  = $"https://huggingface.co/{profile.ModelId}/resolve/{profile.Revision}/{file}";
            var dest = Path.Combine(modelDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            log($"ダウンロード中: {url}");

            using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            // Write to a temp name first so an interrupted download never leaves a
            // truncated file that the File.Exists check above would accept next run.
            var tmp = dest + ".part";
            await using (var fs = File.Create(tmp))
                await res.Content.CopyToAsync(fs, ct);
            File.Move(tmp, dest, overwrite: true);
        }
    }
}
