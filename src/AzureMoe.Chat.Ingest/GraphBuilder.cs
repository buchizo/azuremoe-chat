using System.Globalization;
using LadybugDB;
using AzureMoe.Chat.Core;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Builds a Ladybug database from the fully-processed ingest data.
///
/// Azure Update 特化の追加構造:
/// - <c>AzureService</c> ノード: 登場した Azure サービス名を一元管理
/// - <c>COVERS_SERVICE</c> エッジ: Post → AzureService (日付×サービス名クエリ用)
///
/// 出力は単一 .lbdb ファイル。ブラウザの @ladybugdb/wasm-core が直接開ける。
/// </summary>
public sealed class GraphBuilder : IDisposable
{
    private readonly Database   _db;
    private readonly Connection _conn;

    public GraphBuilder(string dbPath)
    {
        if (File.Exists(dbPath)) File.Delete(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

        _db   = new Database(dbPath, new SystemConfig());
        _conn = new Connection(_db);
    }

    /// <summary>
    /// Runs the full build pipeline.
    /// Returns (ChunkCount, EntityCount, AzureServiceCount).
    /// </summary>
    public (int ChunkCount, int EntityCount, int AzureServiceCount) Build(
        IReadOnlyList<Post>                   posts,
        IReadOnlyList<Chunk>                  chunks,
        IReadOnlyDictionary<long, Extraction> extractions,
        Action<string>?                       log = null)
    {
        log ??= _ => { };

        // --- schema ---------------------------------------------------------
        // Detect embedding dimension from the first chunk that has an embedding.
        // Falls back to the E5 default (384) if no embeddings are present.
        var embeddingDim = chunks.FirstOrDefault(c => c.Embedding?.Length > 0)?.Embedding!.Length
                           ?? GraphSchema.EmbeddingDim;

        log($"スキーマ作成中 (埋め込み次元: {embeddingDim})...");
        Exec("INSTALL vector");
        Exec("LOAD vector");
        foreach (var ddl in GraphSchema.GetSchemaDdl(embeddingDim)) Exec(ddl);

        // --- posts ----------------------------------------------------------
        log($"Post ノード挿入 ({posts.Count} 件)...");
        var postById = posts.ToDictionary(p => p.Id);
        Exec("BEGIN TRANSACTION");
        foreach (var p in posts)
        {
            var (py, pm) = GraphSchema.ParseYearMonth(p.Date);
            Exec($"CREATE (:Post {{id: {p.Id}, title: '{Esc(p.Title)}', url: '{Esc(p.Url)}', " +
                 $"date: '{Esc(p.Date)}', year: {py}, month: {pm}}})");
        }
        Exec("COMMIT");

        // --- chunks (with embeddings) ---------------------------------------
        // Post date/title/year/month are denormalised onto each chunk for fast
        // chunk-level date filtering and citation display without a join.
        log($"Chunk ノード挿入 ({chunks.Count} 件)...");
        Exec("BEGIN TRANSACTION");
        foreach (var c in chunks)
        {
            var emb = c.Embedding ?? throw new InvalidOperationException($"Chunk {c.Id} has no embedding.");
            postById.TryGetValue(c.PostId, out var post);
            var date = post?.Date ?? "";
            var (cy, cm) = GraphSchema.ParseYearMonth(date);
            Exec($"CREATE (:Chunk {{id: {c.Id}, postId: {c.PostId}, ordinal: {c.Ordinal}, " +
                 $"text: '{Esc(c.Text)}', date: '{Esc(date)}', title: '{Esc(post?.Title ?? "")}', " +
                 $"year: {cy}, month: {cm}, emb: {CypherFloatArray(emb)}}})");
        }
        Exec("COMMIT");

        // --- HAS_CHUNK edges ------------------------------------------------
        log("HAS_CHUNK エッジ挿入...");
        Exec("BEGIN TRANSACTION");
        foreach (var c in chunks)
            Exec($"MATCH (p:Post {{id: {c.PostId}}}), (c:Chunk {{id: {c.Id}}}) CREATE (p)-[:HAS_CHUNK]->(c)");
        Exec("COMMIT");

        // --- tags -----------------------------------------------------------
        var allTags = posts.SelectMany(p => p.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (allTags.Count > 0)
        {
            log($"Tag ノード挿入 ({allTags.Count} 件)...");
            Exec("BEGIN TRANSACTION");
            foreach (var tag in allTags)
                Exec($"CREATE (:Tag {{name: '{Esc(tag)}'}})");
            Exec("COMMIT");

            log("TAGGED エッジ挿入...");
            Exec("BEGIN TRANSACTION");
            foreach (var p in posts)
                foreach (var tag in p.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
                    Exec($"MATCH (p:Post {{id: {p.Id}}}), (t:Tag {{name: '{Esc(tag)}'}}) CREATE (p)-[:TAGGED]->(t)");
            Exec("COMMIT");
        }

        // --- Azure services (from LLM extractions) -------------------------
        // Collect unique service names across all extractions, grouped by post.
        var allServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var postServices = new Dictionary<long, HashSet<string>>();

        foreach (var chunk in chunks)
        {
            if (!extractions.TryGetValue(chunk.Id, out var ex)) continue;
            foreach (var svc in ex.AzureServices ?? [])
            {
                if (string.IsNullOrWhiteSpace(svc)) continue;
                allServices.Add(svc);
                if (!postServices.TryGetValue(chunk.PostId, out var set))
                    postServices[chunk.PostId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(svc);
            }
        }

        if (allServices.Count > 0)
        {
            log($"AzureService ノード挿入 ({allServices.Count} 件)...");
            Exec("BEGIN TRANSACTION");
            foreach (var svc in allServices)
                Exec($"CREATE (:AzureService {{name: '{Esc(svc)}'}})");
            Exec("COMMIT");

            log("COVERS_SERVICE エッジ挿入...");
            Exec("BEGIN TRANSACTION");
            foreach (var (postId, services) in postServices)
                foreach (var svc in services)
                    Exec($"MATCH (p:Post {{id: {postId}}}), (s:AzureService {{name: '{Esc(svc)}'}}) " +
                         $"CREATE (p)-[:COVERS_SERVICE]->(s)");
            Exec("COMMIT");
        }

        // --- general entities and MENTIONS ----------------------------------
        var entityMap = new Dictionary<string, (string Type, string Description)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, ex) in extractions)
            foreach (var e in ex.Entities ?? [])
                if (!string.IsNullOrWhiteSpace(e.Name) && !entityMap.ContainsKey(e.Name))
                    entityMap[e.Name] = (e.Type, e.Description);

        if (entityMap.Count > 0)
        {
            log($"Entity ノード挿入 ({entityMap.Count} 件)...");
            Exec("BEGIN TRANSACTION");
            foreach (var (name, (type, desc)) in entityMap)
                Exec($"CREATE (:Entity {{name: '{Esc(name)}', type: '{Esc(type)}', description: '{Esc(desc)}'}})");
            Exec("COMMIT");

            log("MENTIONS エッジ挿入...");
            Exec("BEGIN TRANSACTION");
            foreach (var (chunkId, ex) in extractions)
                foreach (var e in ex.Entities ?? [])
                    if (!string.IsNullOrWhiteSpace(e.Name) && entityMap.ContainsKey(e.Name))
                        Exec($"MATCH (c:Chunk {{id: {chunkId}}}), (e:Entity {{name: '{Esc(e.Name)}'}}) " +
                             $"CREATE (c)-[:MENTIONS]->(e)");
            Exec("COMMIT");

            var relPairs = new HashSet<(string, string)>();
            log("RELATED_TO エッジ挿入...");
            Exec("BEGIN TRANSACTION");
            foreach (var (_, ex) in extractions)
                foreach (var r in ex.Relationships ?? [])
                    if (!string.IsNullOrWhiteSpace(r.Source) && !string.IsNullOrWhiteSpace(r.Target) &&
                        entityMap.ContainsKey(r.Source) && entityMap.ContainsKey(r.Target) &&
                        relPairs.Add((r.Source.ToLowerInvariant(), r.Target.ToLowerInvariant())))
                        Exec($"MATCH (s:Entity {{name: '{Esc(r.Source)}'}}), (t:Entity {{name: '{Esc(r.Target)}'}}) " +
                             $"CREATE (s)-[:RELATED_TO {{description: '{Esc(r.Description)}'}}]->(t)");
            Exec("COMMIT");
        }

        // --- vector index ---------------------------------------------------
        log("ベクトルインデックス構築中...");
        Exec(GraphSchema.CreateVectorIndexCypher);

        return (chunks.Count, entityMap.Count, allServices.Count);
    }

    private void Exec(string cypher)
    {
        using var result = _conn.Query(cypher);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Ladybug query failed: {result.GetErrorMessage()}\n  {cypher[..Math.Min(120, cypher.Length)]}");
    }

    private static string CypherFloatArray(float[] emb)
    {
        var vals = string.Join(",", emb.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        return $"CAST([{vals}] AS FLOAT[{emb.Length}])";
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    public void Dispose()
    {
        _conn.Dispose();
        _db.Dispose();
    }
}
