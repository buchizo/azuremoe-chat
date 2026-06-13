using System.Globalization;
using AzureMoe.Chat.Core;
using AzureMoe.Chat.Ingest;
using AzureMoe.Chat.Verify;
using LadybugDB;
using Microsoft.Extensions.Configuration;

// ---------------------------------------------------------------------------
// オプション
// ---------------------------------------------------------------------------
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var opt = new VerifyOptions();
config.Bind(opt);

if (string.IsNullOrWhiteSpace(opt.DbPath))
    opt.DbPath = FindLatestDb(opt.OutDir);

// ---------------------------------------------------------------------------
// DB を開く
// ---------------------------------------------------------------------------
if (string.IsNullOrWhiteSpace(opt.DbPath) || !File.Exists(opt.DbPath))
{
    Console.Error.WriteLine($"DB ファイルが見つかりません。");
    Console.Error.WriteLine($"  --DbPath <path>  または  --OutDir <dir>  で指定してください。");
    return 1;
}

Console.WriteLine($"DB      : {Path.GetFullPath(opt.DbPath)}");

using var db   = new Database(opt.DbPath, new SystemConfig());
using var conn = new Connection(db);

// QUERY_VECTOR_INDEX に必要な拡張をロード
using (var r = conn.Query("LOAD vector"))
    if (!r.IsSuccess) { Console.Error.WriteLine($"LOAD vector 失敗: {r.GetErrorMessage()}"); return 1; }

PrintStats(conn);

// ---------------------------------------------------------------------------
// E5 埋め込みモデルをロード
// ---------------------------------------------------------------------------
Console.WriteLine($"モデル  : {Path.GetFullPath(opt.ModelDir)}");
using var embedder = new E5Embedder(opt.ModelDir);
_ = embedder.EmbedQuery("ウォームアップ");
Console.WriteLine($"次元    : {embedder.Dimension}");
Console.WriteLine();
Console.WriteLine($"テキストを入力して Enter で検索 (TopK={opt.TopK})  終了: q");
Console.WriteLine(new string('─', 60));

// ---------------------------------------------------------------------------
// インタラクティブ検索ループ
// ---------------------------------------------------------------------------
while (true)
{
    Console.Write("\n> ");
    var input = Console.ReadLine();

    if (input == null || input is "q" or "quit" or "exit") break;
    if (string.IsNullOrWhiteSpace(input)) continue;

    if (input.Equals("\\stats", StringComparison.OrdinalIgnoreCase))
    {
        PrintStats(conn);
        continue;
    }

    var queryVec = embedder.EmbedQuery(input);
    var cypher = $"""
        CALL QUERY_VECTOR_INDEX('Chunk', '{GraphSchema.VectorIndexName}', {CypherFloat(queryVec)}, {opt.TopK})
        YIELD node AS c, distance
        MATCH (p:Post)-[:HAS_CHUNK]->(c)
        RETURN p.title AS title, p.date AS date, p.url AS url, c.text AS text, distance
        ORDER BY distance
        """;

    using var result = conn.Query(cypher);
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine($"クエリエラー: {result.GetErrorMessage()}");
        continue;
    }

    var rank = 0;
    while (result.HasNext())
    {
        rank++;
        using var row = result.GetNext();

        var title    = Str(row, 0);
        var date     = Str(row, 1);
        var url      = Str(row, 2);
        var text     = Str(row, 3);
        var distance = Dbl(row, 4);
        var sim      = 1.0 - distance;
        var dateShort = date.Length >= 10 ? date[..10] : date;
        var snippet  = text.Replace('\n', ' ').Replace('\r', ' ');
        if (snippet.Length > 140) snippet = snippet[..140] + "…";

        Console.WriteLine($"\n[{rank}] ({sim:F3})  {dateShort}  {title}");
        Console.WriteLine($"      {url}");
        Console.WriteLine($"      {snippet}");
    }

    if (rank == 0) Console.WriteLine("  (結果なし)");
}

Console.WriteLine("終了しました。");
return 0;

// ---------------------------------------------------------------------------
// ヘルパー
// ---------------------------------------------------------------------------

static string FindLatestDb(string outDir)
{
    if (!Directory.Exists(outDir)) return string.Empty;
    var files = Directory.GetFiles(outDir, "*.lbdb")
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .ToArray();
    return files.Length > 0 ? files[0] : string.Empty;
}

static void PrintStats(Connection conn)
{
    var parts = new List<string>();
    foreach (var label in new[] { "Post", "Chunk", "Entity", "AzureService" })
    {
        using var r = conn.Query($"MATCH (n:{label}) RETURN COUNT(n)");
        if (r.IsSuccess && r.HasNext())
        {
            using var row = r.GetNext();
            parts.Add($"{label}={Lng(row, 0)}");
        }
    }
    Console.WriteLine($"統計    : {string.Join(", ", parts)}");
}

static string CypherFloat(float[] v)
{
    var vals = string.Join(",", v.Select(f => f.ToString("R", CultureInfo.InvariantCulture)));
    return $"CAST([{vals}] AS FLOAT[{v.Length}])";
}

// --- Value アクセスヘルパー ---
static string Str(LadybugDB.FlatTuple row, int idx)
{
    using var v = row.GetValue(idx);
    return v.IsNull ? "" : v.GetString() ?? "";
}

static double Dbl(LadybugDB.FlatTuple row, int idx)
{
    using var v = row.GetValue(idx);
    return v.IsNull ? 0.0 : Convert.ToDouble(v.GetValue());
}

static long Lng(LadybugDB.FlatTuple row, int idx)
{
    using var v = row.GetValue(idx);
    return v.IsNull ? 0L : Convert.ToInt64(v.GetValue());
}
