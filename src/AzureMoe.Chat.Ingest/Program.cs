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
// `append` subcommand — fetch one blog post from a URL and append it to an
// existing .lbdb, copying the source DB to a dated output file first.
//   dotnet run --project src/AzureMoe.Chat.Ingest -- append <url> <sourceDbPath>
//   ... append <url> <sourceDbPath> [--OutDir <dir>] [--ModelDir <dir>] [--NoLlm] [--Override] [LLM options]
// ---------------------------------------------------------------------------
if (args.Length > 0 && args[0].Equals("append", StringComparison.OrdinalIgnoreCase))
    return await RunAppendAsync(args[1..]);

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
// --NoLlm is a bare flag (no value follows it). The default command-line
// config provider treats "--Key" as needing a value and either swallows the
// next token as its value or binds an empty string to `false` silently — so
// it's parsed by hand here rather than left to the binder.
var noLlm = args.Any(a => a.Equals("--NoLlm", StringComparison.OrdinalIgnoreCase));
var configArgs = args.Where(a => !a.Equals("--NoLlm", StringComparison.OrdinalIgnoreCase)).ToArray();

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(configArgs)
    .Build();

var opt = new IngestOptions();
config.Bind(opt);
opt.NoLlm = noLlm;

// Conventional env var names for LLM overrides.
if (Environment.GetEnvironmentVariable("LLM_BASE_URL") is { } envUrl)   opt.LlmBaseUrl = envUrl;
if (Environment.GetEnvironmentVariable("LLM_MODEL")    is { } envModel) opt.LlmModel   = envModel;
opt.LlmApiKey ??= Environment.GetEnvironmentVariable("LLM_API_KEY");

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------
Directory.CreateDirectory(opt.OutDir);
var dateStamp    = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
var dbFileName   = $"blog-{dateStamp}.lbdb";
var dbPath       = Path.Combine(opt.OutDir, dbFileName);
var manifestPath = Path.Combine(opt.OutDir, "manifest.json");

Console.WriteLine($"XML ディレクトリ : {Path.GetFullPath(opt.XmlDir)}");
Console.WriteLine($"出力先           : {Path.GetFullPath(opt.OutDir)}");
Console.WriteLine($"DB               : {dbFileName}");
Console.WriteLine($"Embedding モデル : {Path.GetFullPath(opt.ModelDir)}  [{opt.EmbeddingDtype}]");
if (opt.NoLlm)
    Console.WriteLine($"LLM              : (スキップ)");
else
    Console.WriteLine($"LLM              : {opt.LlmBaseUrl}  [{opt.LlmModel}]");
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
        if (post.IsUpdatePost)
        {
            var items = UpdatePostParser.Parse(post.Html);
            for (var i = 0; i < items.Count; i++)
            {
                var (svc, text, type) = items[i];
                if (!string.IsNullOrWhiteSpace(text))
                    chunks.Add(new Chunk
                    {
                        Id = chunkId++, PostId = post.Id, Ordinal = i,
                        Text = text, SectionTitle = svc, ServiceName = svc, ChunkType = type,
                    });
            }
        }
        else
        {
            var sections = Chunking.SplitWithSections(post.Html);
            for (var i = 0; i < sections.Count; i++)
            {
                var (text, section) = sections[i];
                chunks.Add(new Chunk
                {
                    Id = chunkId++, PostId = post.Id, Ordinal = i,
                    Text = text, SectionTitle = section, ServiceName = "", ChunkType = "prose",
                });
            }
        }
    }
    Console.WriteLine($" {chunks.Count} チャンク");

    // Small-to-big: 生成用の周辺コンテキスト (contextText) を付与。
    // 埋め込み (検索キー) は Text のみで細粒度のまま。
    ContextEnricher.Enrich(chunks);

    // -----------------------------------------------------------------------
    // Step 3: Embed (multilingual-e5-small ONNX, local)
    // -----------------------------------------------------------------------
    Console.WriteLine($"埋め込み生成中 ({chunks.Count} チャンク、モデル: {opt.ModelDir} [{opt.EmbeddingDtype}])...");
    using var embedder = new E5Embedder(opt.ModelDir, opt.EmbeddingDtype);

    // Update post chunks: prepend service name so the vector captures both the
    // service identity and the update content.
    // Article post chunks: prepend the post title (+ section heading when known).
    var titleById = posts.ToDictionary(p => p.Id, p => p.Title);
    for (var i = 0; i < chunks.Count; i++)
    {
        cts.Token.ThrowIfCancellationRequested();
        chunks[i].Embedding = embedder.EmbedPassage(
            BuildEmbedInput(chunks[i], titleById.GetValueOrDefault(chunks[i].PostId, "")));
        if ((i + 1) % 20 == 0 || i == chunks.Count - 1)
            Console.Write($"\r  [{i + 1}/{chunks.Count}]  dim={embedder.Dimension}  ");
    }
    Console.WriteLine("完了");
    if (embedder.TruncatedCount > 0)
        Console.WriteLine($"  [warn] 512トークン超過で末尾切り捨て: {embedder.TruncatedCount}/{chunks.Count} チャンク");

    // -----------------------------------------------------------------------
    // Step 4: Extract entities + Azure service names (unless --NoLlm)
    // -----------------------------------------------------------------------
    var extractions = new Dictionary<long, Extraction>();
    if (opt.NoLlm)
    {
        Console.WriteLine("エンティティ・サービス名抽出: スキップ (--NoLlm)");
    }
    else
    {
        Console.WriteLine($"エンティティ・サービス名抽出中 ({chunks.Count} チャンク)...");
        Console.WriteLine($"  エンドポイント: {opt.LlmBaseUrl}  モデル: {opt.LlmModel}");
        using var extractor = new EntityExtractor(opt.LlmBaseUrl, opt.LlmModel, opt.LlmApiKey);

        // 抽出はチャンク単位で独立なので、並列数を絞って LLM 呼び出しを重ねる
        // （再インジェスト全体のボトルネック）。結果は id キーなので順序不問。
        {
            using var gate = new SemaphoreSlim(3);
            var done = 0;
            await Task.WhenAll(chunks.Select(async chunk =>
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    var extraction = await extractor.ExtractAsync(chunk.Text, cts.Token);
                    lock (extractions) extractions[chunk.Id] = extraction;
                    var d = Interlocked.Increment(ref done);
                    if (d % 5 == 0 || d == chunks.Count)
                        Console.Write($"\r  [{d}/{chunks.Count}]  ");
                }
                finally { gate.Release(); }
            }));
        }
        Console.WriteLine("完了");
        if (extractor.ParseFailureCount > 0)
            Console.WriteLine($"  [warn] JSON パース失敗: {extractor.ParseFailureCount}/{chunks.Count} チャンク（該当チャンクはグラフエッジなしで続行）");
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
        EmbeddingDtype = opt.EmbeddingDtype,
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
// Embedding input (shared by build and append)
// ---------------------------------------------------------------------------
// Update items: the service name carries the identity of a short bullet.
// Articles: post title, plus the section heading when there is one, so the
// vector captures which part of the article the chunk came from. Dates are
// deliberately NOT embedded — date filtering is structural (year/month columns)
// and e5-small handles numerals poorly.
static string BuildEmbedInput(Chunk chunk, string postTitle)
{
    if (chunk.ChunkType == "update_item" && !string.IsNullOrEmpty(chunk.ServiceName))
        return $"{chunk.ServiceName}\n\n{chunk.Text}";
    if (string.IsNullOrEmpty(postTitle))
        return chunk.Text;
    return string.IsNullOrEmpty(chunk.SectionTitle)
        ? $"{postTitle}\n\n{chunk.Text}"
        : $"{postTitle} — {chunk.SectionTitle}\n\n{chunk.Text}";
}

// ---------------------------------------------------------------------------
// append subcommand implementation
// ---------------------------------------------------------------------------
static async Task<int> RunAppendAsync(string[] rest)
{
    // Parse positional args and flags
    string? url = null, sourceDbPath = null;
    bool overrideMode = false, noLlm = false;
    var configArgs = new List<string>();

    for (int i = 0; i < rest.Length; i++)
    {
        if (rest[i].Equals("--Override", StringComparison.OrdinalIgnoreCase))
        {
            overrideMode = true;
        }
        else if (rest[i].Equals("--NoLlm", StringComparison.OrdinalIgnoreCase))
        {
            noLlm = true;
        }
        else if (rest[i].StartsWith("--") && i + 1 < rest.Length && !rest[i + 1].StartsWith("--"))
        {
            // --Key value pair
            configArgs.Add(rest[i]);
            configArgs.Add(rest[++i]);
        }
        else if (rest[i].StartsWith("--"))
        {
            configArgs.Add(rest[i]);
        }
        else if (url == null)
        {
            url = rest[i];
        }
        else if (sourceDbPath == null)
        {
            sourceDbPath = rest[i];
        }
    }

    if (url == null || sourceDbPath == null)
    {
        Console.Error.WriteLine("使用方法: append <url> <sourceDbPath> [--OutDir <dir>] [--ModelDir <dir>] [--NoLlm] [--Override] [LLM options]");
        return 1;
    }

    // Bind options (reuse same config pattern as main mode)
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(configArgs.ToArray())
        .Build();
    var opt = new IngestOptions();
    config.Bind(opt);
    opt.NoLlm = noLlm;
    if (Environment.GetEnvironmentVariable("LLM_BASE_URL") is { } envUrl)   opt.LlmBaseUrl = envUrl;
    if (Environment.GetEnvironmentVariable("LLM_MODEL")    is { } envModel) opt.LlmModel   = envModel;
    opt.LlmApiKey ??= Environment.GetEnvironmentVariable("LLM_API_KEY");

    Directory.CreateDirectory(opt.OutDir);
    var dateStamp    = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
    var dbFileName   = $"blog-{dateStamp}.lbdb";
    var dbPath       = Path.Combine(opt.OutDir, dbFileName);
    var manifestPath = Path.Combine(opt.OutDir, "manifest.json");

    Console.WriteLine($"URL              : {url}");
    Console.WriteLine($"ソースDB         : {Path.GetFullPath(sourceDbPath)}");
    Console.WriteLine($"出力DB           : {Path.GetFullPath(dbPath)}");
    Console.WriteLine($"Embedding モデル : {Path.GetFullPath(opt.ModelDir)}  [{opt.EmbeddingDtype}]");
    Console.WriteLine($"上書きモード     : {(overrideMode ? "あり (--Override)" : "なし")}");
    if (opt.NoLlm)
        Console.WriteLine($"LLM              : (スキップ)");
    else
        Console.WriteLine($"LLM              : {opt.LlmBaseUrl}  [{opt.LlmModel}]");
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
        // Step 1: Copy source DB to dated output file
        // -----------------------------------------------------------------------
        Console.Write("DB コピー中...");
        if (!File.Exists(sourceDbPath))
            throw new FileNotFoundException($"ソースDB が見つかりません: {sourceDbPath}");

        var srcFull = Path.GetFullPath(sourceDbPath);
        var dstFull = Path.GetFullPath(dbPath);
        if (!srcFull.Equals(dstFull, StringComparison.OrdinalIgnoreCase))
        {
            var srcDir  = Path.GetDirectoryName(srcFull)!;
            var srcName = Path.GetFileName(srcFull);
            var dstDir  = Path.GetDirectoryName(dstFull)!;
            var dstName = Path.GetFileName(dstFull);

            // Remove stale companion files at destination before copying.
            foreach (var f in Directory.GetFiles(dstDir, dstName + "*"))
                File.Delete(f);

            // Copy main file and all companion files (WAL, lock, etc.),
            // renaming each to the destination base name.
            foreach (var srcFile in Directory.GetFiles(srcDir, srcName + "*"))
            {
                var suffix = srcFile[srcFull.Length..]; // e.g. "" / ".wal" / ".lock"
                File.Copy(srcFile, dstFull + suffix, overwrite: true);
            }
        }
        Console.WriteLine($" 完了 ({new FileInfo(dbPath).Length / 1024:N0} KB)");

        // -----------------------------------------------------------------------
        // Step 2: Fetch blog post from URL
        // -----------------------------------------------------------------------
        Console.Write("ブログ記事を取得中...");
        var fetchedPost = await BlogPostFetcher.FetchAsync(url, cts.Token);
        Console.WriteLine($" 完了: \"{fetchedPost.Title}\"");

        // -----------------------------------------------------------------------
        // Steps 3–8: Open DB, process, append — all inside a scoped block so the
        // DB file is released before we try to open it for SHA-256 hashing.
        // -----------------------------------------------------------------------
        Post post;
        List<Chunk> chunks;
        int chunkCount, entityCount, serviceCount;
        int embeddingDim;
        long postCount, totalChunks, entityTotal, serviceTotal;

        {
            // Steps 3–4: check duplicate, assign IDs
            using var appender = new GraphAppender(dbPath);

            var existingPostId = appender.FindPostByUrl(url);
            if (existingPostId.HasValue && !overrideMode)
            {
                Console.Error.WriteLine($"URL は既にDBに存在します (Post.id={existingPostId.Value})。--Override を指定すると上書きできます。");
                return 1;
            }
            if (existingPostId.HasValue)
            {
                Console.Write($"既存 Post.id={existingPostId.Value} を削除中...");
                appender.DeletePost(existingPostId.Value);
                Console.WriteLine(" 完了");
            }

            var (nextPostId, nextChunkId) = appender.GetNextIds();
            post = fetchedPost with { Id = nextPostId };
            Console.WriteLine($"ID 割り当て: Post.id={post.Id}、Chunk 開始 id={nextChunkId}");

            // Step 5: Chunk
            Console.Write("チャンク分割中...");
            if (post.IsUpdatePost)
            {
                var items = UpdatePostParser.Parse(post.Html);
                chunks = new List<Chunk>(items.Count);
                for (var i = 0; i < items.Count; i++)
                {
                    var (svc, text, type) = items[i];
                    if (!string.IsNullOrWhiteSpace(text))
                        chunks.Add(new Chunk
                        {
                            Id = nextChunkId + chunks.Count, PostId = post.Id, Ordinal = i,
                            Text = text, SectionTitle = svc, ServiceName = svc, ChunkType = type,
                        });
                }
            }
            else
            {
                var sections = Chunking.SplitWithSections(post.Html);
                chunks = new List<Chunk>(sections.Count);
                for (var i = 0; i < sections.Count; i++)
                {
                    var (text, section) = sections[i];
                    chunks.Add(new Chunk
                    {
                        Id = nextChunkId + i, PostId = post.Id, Ordinal = i,
                        Text = text, SectionTitle = section, ServiceName = "", ChunkType = "prose",
                    });
                }
            }
            Console.WriteLine($" {chunks.Count} チャンク");

            // Small-to-big: 生成用の周辺コンテキスト (contextText) を付与。
            ContextEnricher.Enrich(chunks);

            // Step 6: Embed
            Console.WriteLine($"埋め込み生成中 ({chunks.Count} チャンク、dtype={opt.EmbeddingDtype})...");
            using var embedder = new E5Embedder(opt.ModelDir, opt.EmbeddingDtype);
            for (var i = 0; i < chunks.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                chunks[i].Embedding = embedder.EmbedPassage(BuildEmbedInput(chunks[i], post.Title));
            }
            embeddingDim = embedder.Dimension > 0 ? embedder.Dimension : GraphSchema.EmbeddingDim;
            Console.WriteLine($"  完了 (dim={embedder.Dimension})");
            if (embedder.TruncatedCount > 0)
                Console.WriteLine($"  [warn] 512トークン超過で末尾切り捨て: {embedder.TruncatedCount}/{chunks.Count} チャンク");

            // Step 7: Extract entities + Azure service names (unless --NoLlm)
            var extractions = new Dictionary<long, Extraction>();
            if (opt.NoLlm)
            {
                Console.WriteLine("エンティティ・サービス名抽出: スキップ (--NoLlm)");
            }
            else
            {
                Console.WriteLine($"エンティティ・サービス名抽出中 ({chunks.Count} チャンク)...");
                Console.WriteLine($"  エンドポイント: {opt.LlmBaseUrl}  モデル: {opt.LlmModel}");
                using var extractor = new EntityExtractor(opt.LlmBaseUrl, opt.LlmModel, opt.LlmApiKey);
                for (var i = 0; i < chunks.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    extractions[chunks[i].Id] = await extractor.ExtractAsync(chunks[i].Text, cts.Token);
                }
                Console.WriteLine("  完了");
                if (extractor.ParseFailureCount > 0)
                    Console.WriteLine($"  [warn] JSON パース失敗: {extractor.ParseFailureCount}/{chunks.Count} チャンク");
            }

            // Step 8: Append to DB
            Console.WriteLine("グラフDB 追記中...");
            (chunkCount, entityCount, serviceCount) = appender.Append(
                post, chunks, extractions, msg => Console.WriteLine($"  {msg}"));
            Console.WriteLine($"  完了: Chunk={chunkCount}, Entity={entityCount}, AzureService={serviceCount}");

            (postCount, totalChunks, entityTotal, serviceTotal) = appender.GetCounts();
        } // appender disposed here — DB file is released before SHA-256

        // -----------------------------------------------------------------------
        // Step 9: Recompute SHA-256 + update manifest
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
            EmbeddingDim   = embeddingDim,
            EmbeddingDtype = opt.EmbeddingDtype,
            DatabaseFile   = dbFileName,
            DatabaseBytes  = dbBytes,
            DatabaseSha256 = dbSha256,
            PostCount      = (int)postCount,
            ChunkCount     = (int)totalChunks,
            EntityCount    = (int)entityTotal,
            ServiceCount   = (int)serviceTotal,
            BuiltAt        = DateTime.UtcNow.ToString("o"),
        };
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, manifestJson, cts.Token);
        Console.WriteLine($"manifest.json 更新完了  ({dbBytes / 1024:N0} KB、SHA-256: {dbSha256[..12]}...)");

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
        return 1;
    }
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
        Console.Error.WriteLine("DB ファイルを指定してください (例: inspect out/blog-YYYYMMDDHHmmss.lbdb)。");
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
                int.TryParse(Flag("--topk"), out var k) ? k : 8,
                Flag("--dtype") ?? GraphSchema.EmbeddingDtype);
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
