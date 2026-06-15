using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace AzureMoe.Chat.Web.Services;

public sealed class AssetLoader : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly AppConfig  _cfg;
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public AssetLoader(HttpClient http, AppConfig cfg, IJSRuntime js)
    {
        _http = http;
        _cfg  = cfg;
        _js   = js;
    }

    public async ValueTask<Manifest> LoadManifestAsync(CancellationToken ct = default)
    {
        var manifest = await _http.GetFromJsonAsync<Manifest>(_cfg.ManifestUrl, ct)
            ?? throw new InvalidOperationException($"manifest.json not found at {_cfg.ManifestUrl}");
        return manifest;
    }

    /// <summary>
    /// Load the database binary. Checks Cache API first (keyed by SHA-256);
    /// downloads from R2 on a miss and caches the result for next time.
    ///
    /// Progress convention:
    ///   (Received=0, Total=0)  → loaded from cache (fast, no real bytes transferred)
    ///   (Received=N, Total=M)  → download in progress, N/M bytes received
    /// </summary>
    public async ValueTask<byte[]> LoadDbAsync(
        Manifest manifest,
        IProgress<(long Received, long Total)>? progress = null,
        CancellationToken ct = default)
    {
        var dbFile = manifest.DatabaseFile
            ?? throw new InvalidOperationException("Manifest has no databaseFile");
        var sha256 = manifest.DatabaseSha256;
        var url    = _cfg.DbBaseUrl.TrimEnd('/') + "/" + dbFile;

        // Try Cache API (SHA-256 key → stale entries are never returned).
        if (sha256 is not null)
        {
            try
            {
                _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/db-loader.js");
                if (await _module.InvokeAsync<bool>("isDbCached", ct, sha256))
                {
                    progress?.Report((0, 0)); // "from cache" sentinel
                    return await _module.InvokeAsync<byte[]>("fetchDbCached", ct, sha256);
                }
            }
            catch
            {
                // Cache API unavailable (e.g. private browsing) — fall through to HTTP.
            }
        }

        // Download with streaming progress.
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

        var bytes = buf.AsSpan(0, offset).ToArray();

        // Store for next visit.
        if (sha256 is not null)
        {
            try
            {
                _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/db-loader.js");
                await _module.InvokeVoidAsync("storeDbInCache", ct, sha256, bytes);
            }
            catch { /* Storage failure — continue without caching */ }
        }

        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }
}
