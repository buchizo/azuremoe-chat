using System.Net.Http.Json;

namespace AzureMoe.Chat.Web.Services;

public sealed class AssetLoader
{
    private readonly HttpClient _http;
    private readonly AppConfig  _cfg;

    public AssetLoader(HttpClient http, AppConfig cfg)
    {
        _http = http;
        _cfg  = cfg;
    }

    public async ValueTask<Manifest> LoadManifestAsync(CancellationToken ct = default)
    {
        var manifest = await _http.GetFromJsonAsync<Manifest>(_cfg.ManifestUrl, ct)
            ?? throw new InvalidOperationException($"manifest.json not found at {_cfg.ManifestUrl}");
        return manifest;
    }

    public async ValueTask<byte[]> LoadDbAsync(
        Manifest manifest,
        IProgress<(long Received, long Total)>? progress = null,
        CancellationToken ct = default)
    {
        var dbFile = manifest.DatabaseFile
            ?? throw new InvalidOperationException("Manifest has no databaseFile");

        var url = _cfg.DbBaseUrl.TrimEnd('/') + "/" + dbFile;
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? manifest.DatabaseBytes;
        using var stream = await resp.Content.ReadAsStreamAsync(ct);

        var buf    = new byte[total > 0 ? total : 1024 * 1024];
        var offset = 0;
        int read;
        var tmpBuf = new byte[81920];
        while ((read = await stream.ReadAsync(tmpBuf, ct)) > 0)
        {
            if (offset + read > buf.Length)
                Array.Resize(ref buf, Math.Max(buf.Length * 2, offset + read));
            tmpBuf.AsSpan(0, read).CopyTo(buf.AsSpan(offset));
            offset += read;
            progress?.Report((offset, total));
        }

        return buf.AsSpan(0, offset).ToArray();
    }
}
