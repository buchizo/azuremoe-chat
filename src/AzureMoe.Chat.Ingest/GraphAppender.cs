using System.Globalization;
using LadybugDB;
using AzureMoe.Chat.Core;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Opens an existing .lbdb and appends a single post's data to it.
/// Unlike GraphBuilder, this never deletes the file — it adds to what's already there.
/// </summary>
public sealed class GraphAppender : IDisposable
{
    private readonly Database   _db;
    private readonly Connection _conn;

    public GraphAppender(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"DB ファイルが見つかりません: {dbPath}");

        _db   = new Database(dbPath, new SystemConfig());
        _conn = new Connection(_db);

        // Load vector extension so QUERY_VECTOR_INDEX is available when inspecting
        // or checking state before an append.
        try { _conn.Query("LOAD vector").Dispose(); }
        catch { try { _conn.Query("INSTALL vector").Dispose(); _conn.Query("LOAD vector").Dispose(); } catch { } }
    }

    /// <summary>Returns the Post.id of an existing post with the given URL, or null.</summary>
    public long? FindPostByUrl(string url)
    {
        using var r = Query($"MATCH (p:Post {{url: '{Esc(url)}'}}) RETURN p.id LIMIT 1");
        if (!r.HasNext()) return null;
        using var row = r.GetNext();
        using var v   = row.GetValue(0);
        return v.IsNull ? null : Convert.ToInt64(v.GetValue(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Deletes a post and all its chunk nodes (plus all edges attached to them).
    /// Orphaned Tag/Entity/AzureService nodes shared with other posts are left intact.
    /// </summary>
    public void DeletePost(long postId)
    {
        // Chunks first (carries MENTIONS edges etc.)
        Exec($"MATCH (c:Chunk) WHERE c.postId = {postId} DETACH DELETE c");
        // Then the post itself (carries TAGGED / COVERS_SERVICE / HAS_CHUNK edges)
        Exec($"MATCH (p:Post {{id: {postId}}}) DETACH DELETE p");
    }

    /// <summary>Returns (nextPostId, nextChunkId) based on current max ids in the DB.</summary>
    public (long NextPostId, long NextChunkId) GetNextIds()
    {
        long nextPost = 1, nextChunk = 0;

        using (var r = Query("MATCH (p:Post) RETURN max(p.id)"))
        {
            if (r.HasNext())
            {
                using var row = r.GetNext();
                using var v   = row.GetValue(0);
                if (!v.IsNull)
                    nextPost = Convert.ToInt64(v.GetValue(), CultureInfo.InvariantCulture) + 1;
            }
        }

        using (var r = Query("MATCH (c:Chunk) RETURN max(c.id)"))
        {
            if (r.HasNext())
            {
                using var row = r.GetNext();
                using var v   = row.GetValue(0);
                if (!v.IsNull)
                    nextChunk = Convert.ToInt64(v.GetValue(), CultureInfo.InvariantCulture) + 1;
            }
        }

        return (nextPost, nextChunk);
    }

    /// <summary>Returns total node counts across the DB (for manifest regeneration).</summary>
    public (long Posts, long Chunks, long Entities, long Services) GetCounts() =>
        (ScalarLong("MATCH (n:Post) RETURN count(n)"),
         ScalarLong("MATCH (n:Chunk) RETURN count(n)"),
         ScalarLong("MATCH (n:Entity) RETURN count(n)"),
         ScalarLong("MATCH (n:AzureService) RETURN count(n)"));

    /// <summary>
    /// Appends one post and its pre-embedded chunks to the database.
    /// Drops and recreates the vector index so new chunks are searchable.
    /// Returns (ChunkCount, EntityCount, AzureServiceCount) for the appended post.
    /// </summary>
    public (int ChunkCount, int EntityCount, int AzureServiceCount) Append(
        Post                                  post,
        IReadOnlyList<Chunk>                  chunks,
        IReadOnlyDictionary<long, Extraction> extractions,
        Action<string>?                       log = null)
    {
        log ??= _ => { };

        // Drop existing vector index — must happen before inserting new chunk nodes
        // so the recreated index covers everything.
        log("ベクトルインデックスを削除中...");
        try { Exec($"CALL DROP_VECTOR_INDEX('Chunk', '{GraphSchema.VectorIndexName}')"); }
        catch { /* index may not exist yet */ }

        // --- Post node -------------------------------------------------------
        log("Post ノード挿入...");
        var (py, pm) = GraphSchema.ParseYearMonth(post.Date);
        Exec("BEGIN TRANSACTION");
        Exec($"CREATE (:Post {{id: {post.Id}, title: '{Esc(post.Title)}', url: '{Esc(post.Url)}', " +
             $"date: '{Esc(post.Date)}', year: {py}, month: {pm}}})");
        Exec("COMMIT");

        // --- Chunk nodes -----------------------------------------------------
        log($"Chunk ノード挿入 ({chunks.Count} 件)...");
        Exec("BEGIN TRANSACTION");
        foreach (var c in chunks)
        {
            var emb = c.Embedding ?? throw new InvalidOperationException($"Chunk {c.Id} has no embedding.");
            Exec($"CREATE (:Chunk {{id: {c.Id}, postId: {c.PostId}, ordinal: {c.Ordinal}, " +
                 $"text: '{Esc(c.Text)}', date: '{Esc(post.Date)}', title: '{Esc(post.Title)}', " +
                 $"year: {py}, month: {pm}, " +
                 $"sectionTitle: '{Esc(c.SectionTitle)}', serviceName: '{Esc(c.ServiceName)}', chunkType: '{Esc(c.ChunkType)}', " +
                 $"emb: {CypherFloatArray(emb)}}})");
        }
        Exec("COMMIT");

        // --- HAS_CHUNK edges -------------------------------------------------
        Exec("BEGIN TRANSACTION");
        foreach (var c in chunks)
            Exec($"MATCH (p:Post {{id: {c.PostId}}}), (c:Chunk {{id: {c.Id}}}) CREATE (p)-[:HAS_CHUNK]->(c)");
        Exec("COMMIT");

        // --- Tags: create only if not already in DB --------------------------
        if (post.Tags.Count > 0)
        {
            var newTags = post.Tags
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => ScalarLong($"MATCH (t:Tag {{name: '{Esc(t)}'}}) RETURN count(t)") == 0)
                .ToList();

            if (newTags.Count > 0)
            {
                log($"Tag ノード挿入 ({newTags.Count} 件)...");
                Exec("BEGIN TRANSACTION");
                foreach (var tag in newTags)
                    Exec($"CREATE (:Tag {{name: '{Esc(tag)}'}})");
                Exec("COMMIT");
            }

            Exec("BEGIN TRANSACTION");
            foreach (var tag in post.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
                Exec($"MATCH (p:Post {{id: {post.Id}}}), (t:Tag {{name: '{Esc(tag)}'}}) CREATE (p)-[:TAGGED]->(t)");
            Exec("COMMIT");
        }

        // --- Azure services --------------------------------------------------
        // Update posts: from H2 headings (chunk.ServiceName).
        // Article posts: from LLM extraction.
        var allServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            if (!string.IsNullOrEmpty(chunk.ServiceName))
            {
                allServices.Add(chunk.ServiceName);
            }
            else if (extractions.TryGetValue(chunk.Id, out var ex))
            {
                foreach (var svc in ex.AzureServices ?? [])
                    if (!string.IsNullOrWhiteSpace(svc)) allServices.Add(svc);
            }
        }

        if (allServices.Count > 0)
        {
            var newSvcs = allServices
                .Where(s => ScalarLong($"MATCH (s:AzureService {{name: '{Esc(s)}'}}) RETURN count(s)") == 0)
                .ToList();

            if (newSvcs.Count > 0)
            {
                log($"AzureService ノード挿入 ({newSvcs.Count} 件)...");
                Exec("BEGIN TRANSACTION");
                foreach (var svc in newSvcs)
                    Exec($"CREATE (:AzureService {{name: '{Esc(svc)}'}})");
                Exec("COMMIT");
            }

            Exec("BEGIN TRANSACTION");
            foreach (var svc in allServices)
                Exec($"MATCH (p:Post {{id: {post.Id}}}), (s:AzureService {{name: '{Esc(svc)}'}}) " +
                     $"CREATE (p)-[:COVERS_SERVICE]->(s)");
            Exec("COMMIT");
        }

        // --- Entities --------------------------------------------------------
        var entityMap = new Dictionary<string, (string Type, string Description)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, ex) in extractions)
            foreach (var e in ex.Entities ?? [])
                if (!string.IsNullOrWhiteSpace(e.Name) && !entityMap.ContainsKey(e.Name))
                    entityMap[e.Name] = (e.Type, e.Description);

        if (entityMap.Count > 0)
        {
            var newEntities = entityMap.Keys
                .Where(n => ScalarLong($"MATCH (e:Entity {{name: '{Esc(n)}'}}) RETURN count(e)") == 0)
                .ToList();

            if (newEntities.Count > 0)
            {
                log($"Entity ノード挿入 ({newEntities.Count} 件)...");
                Exec("BEGIN TRANSACTION");
                foreach (var name in newEntities)
                {
                    var (type, desc) = entityMap[name];
                    Exec($"CREATE (:Entity {{name: '{Esc(name)}', type: '{Esc(type)}', description: '{Esc(desc)}'}})");
                }
                Exec("COMMIT");
            }

            log("MENTIONS エッジ挿入...");
            Exec("BEGIN TRANSACTION");
            foreach (var (chunkId, ex) in extractions)
                foreach (var e in ex.Entities ?? [])
                    if (!string.IsNullOrWhiteSpace(e.Name) && entityMap.ContainsKey(e.Name))
                        Exec($"MATCH (c:Chunk {{id: {chunkId}}}), (e:Entity {{name: '{Esc(e.Name)}'}}) " +
                             $"CREATE (c)-[:MENTIONS]->(e)");
            Exec("COMMIT");

            var relPairs = new HashSet<(string, string)>();
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

        // --- Rebuild vector index --------------------------------------------
        log("ベクトルインデックス再作成中...");
        Exec(GraphSchema.CreateVectorIndexCypher);

        return (chunks.Count, entityMap.Count, allServices.Count);
    }

    // -------------------------------------------------------------------------

    private void Exec(string cypher)
    {
        using var result = _conn.Query(cypher);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Ladybug query failed: {result.GetErrorMessage()}\n  {cypher[..Math.Min(120, cypher.Length)]}");
    }

    private QueryResult Query(string cypher)
    {
        var r = _conn.Query(cypher);
        if (!r.IsSuccess)
        {
            var msg = r.GetErrorMessage();
            r.Dispose();
            throw new InvalidOperationException($"Cypher 実行失敗: {msg}");
        }
        return r;
    }

    private long ScalarLong(string cypher)
    {
        using var r = Query(cypher);
        if (!r.HasNext()) return 0;
        using var row = r.GetNext();
        using var v   = row.GetValue(0);
        return v.IsNull ? 0 : Convert.ToInt64(v.GetValue(), CultureInfo.InvariantCulture);
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
