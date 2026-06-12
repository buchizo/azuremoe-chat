using System.Globalization;
using LadybugDB;

// POC-1: build a Ladybug database with a vector index on desktop (native engine),
// to verify the same file can be opened by @ladybugdb/wasm-core in the browser.

var dbPath = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("out", "poc1.db"));
if (Directory.Exists(dbPath)) Directory.Delete(dbPath, recursive: true);
if (File.Exists(dbPath)) File.Delete(dbPath);
if (File.Exists(dbPath + ".wal")) File.Delete(dbPath + ".wal");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

using var db = new Database(dbPath, new SystemConfig());
using var conn = new Connection(db);

void Exec(string query, bool printRows = false)
{
    Console.WriteLine($"> {query}");
    using var result = conn.Query(query);
    if (!result.IsSuccess)
        throw new InvalidOperationException(result.GetErrorMessage());
    if (printRows)
    {
        Console.WriteLine("  " + string.Join(" | ", result.ColumnNames));
        foreach (var row in result.Rows())
            Console.WriteLine("  " + string.Join(" | ", row.Select(v => v?.ToString() ?? "null")));
    }
}

try { Exec("CALL db_version() RETURN *", printRows: true); }
catch (Exception e) { Console.WriteLine($"  (db_version unavailable: {e.Message})"); }

Exec("INSTALL vector");
Exec("LOAD vector");

Exec("CREATE NODE TABLE Chunk(id INT64, text STRING, emb FLOAT[8], PRIMARY KEY(id))");
Exec("CREATE NODE TABLE Entity(name STRING, PRIMARY KEY(name))");
Exec("CREATE REL TABLE MENTIONS(FROM Chunk TO Entity)");
Exec("CREATE (:Entity {name: 'Azure'})");
Exec("CREATE (:Entity {name: 'Cloudflare'})");

// Deterministic embeddings: chunk i points mostly along basis axis (i % 8),
// so a query vector along axis k must return chunks with i % 8 == k first.
const int chunkCount = 16;
for (var i = 0; i < chunkCount; i++)
{
    var emb = Enumerable.Range(0, 8)
        .Select(j => j == i % 8 ? 1.0f : 0.1f)
        .Select(v => v.ToString("0.0###", CultureInfo.InvariantCulture));
    var entity = i % 2 == 0 ? "Azure" : "Cloudflare";
    Exec($"CREATE (:Chunk {{id: {i}, text: 'チャンク{i} 軸{i % 8} {entity}の話', emb: CAST([{string.Join(",", emb)}] AS FLOAT[8])}})");
    Exec($"MATCH (c:Chunk {{id: {i}}}), (e:Entity {{name: '{entity}'}}) CREATE (c)-[:MENTIONS]->(e)");
}

Exec("CALL CREATE_VECTOR_INDEX('Chunk', 'chunk_idx', 'emb', metric := 'cosine')");

Console.WriteLine("--- native verification: vector search (query along axis 2, expect ids 2 and 10 first)");
Exec("CALL QUERY_VECTOR_INDEX('Chunk', 'chunk_idx', CAST([0.1,0.1,1.0,0.1,0.1,0.1,0.1,0.1] AS FLOAT[8]), 4) " +
     "RETURN node.id AS id, node.text AS text, distance ORDER BY distance", printRows: true);

Console.WriteLine("--- native verification: graph traversal");
Exec("MATCH (c:Chunk)-[:MENTIONS]->(e:Entity) RETURN e.name AS entity, count(*) AS chunks ORDER BY entity", printRows: true);

Console.WriteLine($"Done. Database written to: {dbPath}");
