using System.Security.Cryptography;
using System.Text.Json;
using AzureMoe.Chat.Core;
using AzureMoe.Chat.Ingest;
using Microsoft.Extensions.Configuration;

// ---------------------------------------------------------------------------
// `inspect` subcommand — read-only diagnostics over a built .lbdb.
//   dotnet run --project src/AzureMoe.Chat.Ingest -- inspect [dbPath]
//   ... inspect <dbPath> --cypher "MATCH (n) RETURN count(n)"
//   ... inspect <dbPath> --query "2026年2月のAzure Functionsの更新" [--model <dir>] [--topk 8]
// ---------------------------------------------------------------------------
if (args.Length > 0 && args[0].Equals("inspect", StringComparison.OrdinalIgnoreCase))
    return RunInspect(args[1..]);

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var opt = new IngestOptions();
config.Bind(opt);

// Conventional env var names for LLM overrides.
if (Environment.GetEnvironmentVariable("LLM_BASE_URL") is { } envUrl)   opt.LlmBaseUrl = envUrl;
if (Environment.GetEnvironmentVariable("LLM_MODEL")    is { } envModel) opt.LlmModel   = envModel;
opt.LlmApiKey         ??= Environment.GetEnvironmentVariable("LLM_API_KEY");
opt.R2AccountId       ??= Environment.GetEnvironmentVariable("R2_ACCOUNT_ID");
opt.R2AccessKeyId     ??= Environment.GetEnvironmentVariable("R2_ACCESS_KEY_ID");
opt.R2SecretAccessKey ??= Environment.GetEnvironmentVariable("R2_SECRET_ACCESS_KEY");
opt.R2Bucket          ??= Environment.GetEnvironmentVariable("R2_BUCKET");

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------
Directory.CreateDirectory(opt.OutDir);
var dateStamp    = DateTime.UtcNow.ToString("yyyyMMdd");
var dbFileName   = $"blog-{dateStamp}.lbdb";
var dbPath       = Path.Combine(opt.OutDir, dbFileName);
var manifestPath = Path.Combine(opt.OutDir, "manifest.json");

var uploadEnabled = !opt.SkipR2 && opt.HasR2;

Console.WriteLine($"XML ディレクトリ : {Path.GetFullPath(opt.XmlDir)}");
Console.WriteLine($"出力先           : {Path.GetFullPath(opt.OutDir)}");
Console.WriteLine($"DB               : {dbFileName}");
Console.WriteLine($"Embedding モデル : {Path.GetFullPath(opt.ModelDir)}");
if (opt.NoLlm)
    Console.WriteLine($"LLM              : (スキップ)");
else
    Console.WriteLine($"LLM              : {opt.LlmBaseUrl}  [{opt.LlmModel}]");
Console.WriteLine($"R2 アップロード  : {(uploadEnabled ? opt.R2Bucket : opt.SkipR2 ? "スキップ (--SkipR2)" : "なし (認証情報未設定)")}");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.Error.WriteLine("\n中断しています...");
};

try
{
    // -----------------------------------------------------------------------
    // Step 1: Read posts from XML export files
    // -----------------------------------------------------------------------
    Console.Write("XML ファイルを読み込み中...");
    var posts = WordPressXmlReader.ReadFromDirectory(
        opt.XmlDir, opt.MaxPosts, msg => Console.WriteLine($"\n{msg}"));
    Console.WriteLine($" {posts.Count} 件の投稿を読み込みました");

    if (posts.Count == 0)
    {
        Console.Error.WriteLine($"投稿が見つかりませんでした。{opt.XmlDir}/ に WordPress エクスポート XML を配置してください。");
        return 1;
    }

    // -----------------------------------------------------------------------
    // Step 2: Chunk
    // -----------------------------------------------------------------------
    Console.Write("チャンク分割中...");
    var chunks = new List<Chunk>();
    long chunkId = 0;
    foreach (var post in posts)
    {
        var text   = Chunking.HtmlToText(post.Html);
        var pieces = Chunking.SplitIntoChunks(text);
        foreach (var (piece, ordinal) in pieces.Select((p, i) => (p, i)))
            chunks.Add(new Chunk { Id = chunkId++, PostId = post.Id, Ordinal = ordinal, Text = piece });
    }
    Console.WriteLine($" {chunks.Count} チャンク");

    // -----------------------------------------------------------------------
    // Step 3: Embed (multilingual-e5-small ONNX, local)
    // -----------------------------------------------------------------------
    Console.WriteLine($"埋め込み生成中 ({chunks.Count} チャンク、モデル: {opt.ModelDir})...");
    using var embedder = new E5Embedder(opt.ModelDir);

    // Prepend the post title so each chunk's vector carries the article's identity
    // (topic/date). Improves relevance for "what was posted about X" style queries.
    var titleById = posts.ToDictionary(p => p.Id, p => p.Title);
    for (var i = 0; i < chunks.Count; i++)
    {
        cts.Token.ThrowIfCancellationRequested();
        var title = titleById.GetValueOrDefault(chunks[i].PostId, "");
        var embedInput = string.IsNullOrEmpty(title) ? chunks[i].Text : $"{title}\n\n{chunks[i].Text}";
        chunks[i].Embedding = embedder.EmbedPassage(embedInput);
        if ((i + 1) % 20 == 0 || i == chunks.Count - 1)
            Console.Write($"\r  [{i + 1}/{chunks.Count}]  dim={embedder.Dimension}  ");
    }
    Console.WriteLine("完了");

    // -----------------------------------------------------------------------
    // Step 4: Extract entities + Azure service names (unless --NoLlm)
    // -----------------------------------------------------------------------
    var extractions = new Dictionary<long, Extraction>();
    if (!opt.NoLlm)
    {
        Console.WriteLine($"エンティティ・サービス名抽出中 ({chunks.Count} チャンク)...");
        Console.WriteLine($"  エンドポイント: {opt.LlmBaseUrl}  モデル: {opt.LlmModel}");
        using var extractor = new EntityExtractor(opt.LlmBaseUrl, opt.LlmModel, opt.LlmApiKey);

        for (var i = 0; i < chunks.Count; i++)
        {
            cts.Token.ThrowIfCancellationRequested();
            try
            {
                extractions[chunks[i].Id] = await extractor.ExtractAsync(chunks[i].Text, cts.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n  チャンク {i} 抽出エラー (スキップ): {ex.Message}");
                extractions[chunks[i].Id] = new Extraction([], [], []);
            }

            if ((i + 1) % 5 == 0 || i == chunks.Count - 1)
                Console.Write($"\r  [{i + 1}/{chunks.Count}]  ");
        }
        Console.WriteLine("完了");
    }

    // -----------------------------------------------------------------------
    // Step 5: Build graph database
    // -----------------------------------------------------------------------
    Console.WriteLine("グラフDB構築中...");
    int chunkCount, entityCount, serviceCount;
    using (var builder = new GraphBuilder(dbPath))
        (chunkCount, entityCount, serviceCount) = builder.Build(
            posts, chunks, extractions, msg => Console.WriteLine($"  {msg}"));
    Console.WriteLine($"  完了: Post={posts.Count}, Chunk={chunkCount}, Entity={entityCount}, AzureService={serviceCount}");

    // -----------------------------------------------------------------------
    // Step 6: SHA-256 + manifest
    // -----------------------------------------------------------------------
    var dbBytes = new FileInfo(dbPath).Length;
    string dbSha256;
    using (var sha = SHA256.Create())
    await using (var fs = File.OpenRead(dbPath))
    {
        var hash = await sha.ComputeHashAsync(fs, cts.Token);
        dbSha256 = Convert.ToHexString(hash).ToLowerInvariant();
    }

    var manifest = new Manifest
    {
        EngineVersion  = GraphSchema.EngineVersion,
        EmbeddingModel = GraphSchema.EmbeddingModel,
        EmbeddingDim   = embedder.Dimension > 0 ? embedder.Dimension : GraphSchema.EmbeddingDim,
        DatabaseFile   = dbFileName,
        DatabaseBytes  = dbBytes,
        DatabaseSha256 = dbSha256,
        PostCount      = posts.Count,
        ChunkCount     = chunkCount,
        EntityCount    = entityCount,
        ServiceCount   = serviceCount,
        BuiltAt        = DateTime.UtcNow.ToString("o"),
    };
    var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(manifestPath, manifestJson, cts.Token);
    Console.WriteLine($"manifest.json 書き込み完了  ({dbBytes / 1024:N0} KB、SHA-256: {dbSha256[..12]}...)");

    // -----------------------------------------------------------------------
    // Step 7: Upload to R2 (optional, skipped with --SkipR2)
    // -----------------------------------------------------------------------
    if (uploadEnabled)
    {
        Console.WriteLine($"R2 へアップロード中 (バケット: {opt.R2Bucket})...");
        using var uploader = new R2Uploader(
            opt.R2AccountId!, opt.R2AccessKeyId!, opt.R2SecretAccessKey!, opt.R2Bucket!);
        await uploader.UploadAsync(dbPath,       dbFileName,     "application/octet-stream", cts.Token);
        Console.WriteLine($"  ✓ {dbFileName}");
        await uploader.UploadAsync(manifestPath, "manifest.json", "application/json", cts.Token);
        Console.WriteLine($"  ✓ manifest.json");
    }
    else
    {
        Console.WriteLine("(R2 アップロードをスキップ — ローカル出力のみ)");
    }

    Console.WriteLine();
    Console.WriteLine("完了。");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("中断されました。");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nエラー: {ex.Message}");
    if (ex.InnerException is { } inner)
        Console.Error.WriteLine($"  詳細: {inner.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(ex.ToString());
    return 1;
}

// ---------------------------------------------------------------------------
// inspect subcommand implementation
// ---------------------------------------------------------------------------
static int RunInspect(string[] rest)
{
    string? Flag(string name)
    {
        var i = Array.FindIndex(rest, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
    }

    // First non-flag arg is the DB path; otherwise auto-pick newest .lbdb.
    var dbPath = rest.FirstOrDefault(a => !a.StartsWith('-')) ?? FindNewestDb();
    if (dbPath is null)
    {
        Console.Error.WriteLine("DB ファイルを指定してください (例: inspect out/blog-YYYYMMDD.lbdb)。");
        return 1;
    }

    Console.WriteLine($"DB: {Path.GetFullPath(dbPath)}");
    Console.WriteLine();

    try
    {
        using var inspector = new GraphInspector(dbPath);

        if (Flag("--cypher") is { } cypher)
            inspector.RunCypher(cypher);
        else if (Flag("--query") is { } query)
            inspector.SampleVectorSearch(
                query,
                Flag("--model") ?? "model/Xenova/multilingual-e5-small",
                int.TryParse(Flag("--topk"), out var k) ? k : 8);
        else
            inspector.PrintStats();

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"エラー: {ex.Message}");
        return 1;
    }

    static string? FindNewestDb()
    {
        foreach (var dir in new[] { "out", "src/AzureMoe.Chat.Web/wwwroot/data" })
        {
            if (!Directory.Exists(dir)) continue;
            var newest = Directory.GetFiles(dir, "*.lbdb")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (newest is not null) return newest;
        }
        return null;
    }
}
