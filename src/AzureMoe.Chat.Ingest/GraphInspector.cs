using System.Globalization;
using LadybugDB;
using AzureMoe.Chat.Core;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Read-only diagnostics over a built .lbdb. Lets you verify that the ingested
/// graph actually contains what the RAG layer expects: node/edge counts, the
/// date distribution (to debug "wrong month" answers), the most-connected
/// entities/services, and ad-hoc Cypher / sample vector searches.
///
/// Not part of the build pipeline — invoked via the <c>inspect</c> subcommand.
/// </summary>
public sealed class GraphInspector : IDisposable
{
    private readonly Database   _db;
    private readonly Connection _conn;

    public GraphInspector(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"DB ファイルが見つかりません: {dbPath}");

        _db   = new Database(dbPath, new SystemConfig { ReadOnly = true });
        _conn = new Connection(_db);

        // The vector extension lives outside the DB file; load it each session so
        // QUERY_VECTOR_INDEX is available (the HNSW index itself persists in the file).
        try { _conn.Query("LOAD vector").Dispose(); }
        catch { try { _conn.Query("INSTALL vector").Dispose(); _conn.Query("LOAD vector").Dispose(); } catch { } }
    }

    /// <summary>Print a high-level summary of the graph contents.</summary>
    public void PrintStats()
    {
        Console.WriteLine("=== ノード件数 ===");
        foreach (var (label, _) in NodeLabels)
            Console.WriteLine($"  {label,-14}: {ScalarLong($"MATCH (n:{label}) RETURN count(n)"),8:N0}");

        Console.WriteLine();
        Console.WriteLine("=== エッジ件数 ===");
        foreach (var rel in RelLabels)
            Console.WriteLine($"  {rel,-16}: {ScalarLong($"MATCH ()-[r:{rel}]->() RETURN count(r)"),8:N0}");

        Console.WriteLine();
        Console.WriteLine("=== 日付分布 (Post.date 月別) ===");
        PrintTable(
            "MATCH (p:Post) WHERE p.date IS NOT NULL AND size(p.date) >= 7 " +
            "RETURN substring(p.date, 1, 7) AS month, count(p) AS posts " +
            "ORDER BY month",
            maxRows: 240);

        Console.WriteLine();
        Console.WriteLine("=== 次数の高い AzureService (上位20) ===");
        PrintTable(
            "MATCH (p:Post)-[:COVERS_SERVICE]->(s:AzureService) " +
            "RETURN s.name AS service, count(p) AS posts ORDER BY posts DESC LIMIT 20",
            maxRows: 20);

        Console.WriteLine();
        Console.WriteLine("=== 次数の高い Entity (上位20) ===");
        PrintTable(
            "MATCH (c:Chunk)-[:MENTIONS]->(e:Entity) " +
            "RETURN e.name AS entity, e.type AS type, count(c) AS mentions " +
            "ORDER BY mentions DESC LIMIT 20",
            maxRows: 20);

        Console.WriteLine();
        Console.WriteLine("=== サンプル Chunk (3件) ===");
        PrintTable(
            "MATCH (p:Post)-[:HAS_CHUNK]->(c:Chunk) " +
            "RETURN p.date AS date, p.title AS title, substring(c.text, 1, 80) AS text_head LIMIT 3",
            maxRows: 3);
    }

    /// <summary>Run an arbitrary read-only Cypher query and print the result as a table.</summary>
    public void RunCypher(string cypher)
    {
        Console.WriteLine($"Cypher> {cypher}");
        Console.WriteLine();
        PrintTable(cypher, maxRows: 100);
    }

    /// <summary>
    /// Embed a natural-language query with the same E5 model used at ingest and
    /// run the vector index, printing the top-K chunks. Verifies that a given
    /// question retrieves the articles you'd expect.
    /// </summary>
    public void SampleVectorSearch(string queryText, string modelDir, int topK)
    {
        using var embedder = new E5Embedder(modelDir);
        var vec  = embedder.EmbedQuery(queryText);
        var vals = string.Join(",", vec.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        var cypher = $"""
            CALL QUERY_VECTOR_INDEX('Chunk', '{GraphSchema.VectorIndexName}', CAST([{vals}] AS FLOAT[{vec.Length}]), {topK})
            YIELD node AS c, distance
            MATCH (p:Post)-[:HAS_CHUNK]->(c)
            RETURN p.date AS date, (1.0 - distance) AS sim, p.title AS title, substring(c.text, 1, 60) AS text_head
            ORDER BY distance
            """;
        Console.WriteLine($"クエリ: \"{queryText}\"  (top {topK})");
        Console.WriteLine();
        PrintTable(cypher, maxRows: topK);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static readonly (string Label, string _)[] NodeLabels =
        [("Post", ""), ("Chunk", ""), ("Entity", ""), ("AzureService", ""), ("Tag", "")];

    private static readonly string[] RelLabels =
        ["HAS_CHUNK", "MENTIONS", "RELATED_TO", "TAGGED", "COVERS_SERVICE"];

    private long ScalarLong(string cypher)
    {
        using var r = Query(cypher);
        if (!r.HasNext()) return 0;
        using var row = r.GetNext();
        using var v = row.GetValue(0);
        return v.IsNull ? 0 : Convert.ToInt64(v.GetValue(), CultureInfo.InvariantCulture);
    }

    private void PrintTable(string cypher, int maxRows)
    {
        using var r = Query(cypher);
        var cols = r.ColumnNames;
        var rows = new List<string[]>();
        var n = 0;
        while (r.HasNext() && n++ < maxRows)
        {
            using var tuple = r.GetNext();
            var cells = new string[cols.Count];
            for (var i = 0; i < cols.Count; i++)
            {
                using var v = tuple.GetValue(i);
                cells[i] = v.IsNull ? "" : (v.GetValue()?.ToString() ?? "");
            }
            rows.Add(cells);
        }

        var widths = new int[cols.Count];
        for (var i = 0; i < cols.Count; i++)
            widths[i] = Math.Min(60, Math.Max(cols[i].Length, rows.Count == 0 ? 0 : rows.Max(row => Len(row[i]))));

        Console.WriteLine(string.Join("  ", cols.Select((c, i) => Pad(c, widths[i]))));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
            Console.WriteLine(string.Join("  ", row.Select((c, i) => Pad(c, widths[i]))));
        if (n > maxRows)
            Console.WriteLine($"... (上限 {maxRows} 行で打ち切り)");
    }

    private static int Len(string s) => s.Length > 60 ? 60 : s.Length;
    private static string Pad(string s, int w)
    {
        s = s.Replace("\n", " ").Replace("\r", " ");
        if (s.Length > w) s = w > 1 ? s[..(w - 1)] + "…" : s[..w];
        return s.PadRight(w);
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

    public void Dispose()
    {
        _conn.Dispose();
        _db.Dispose();
    }
}
