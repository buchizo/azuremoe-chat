using System.Globalization;
using System.Text.RegularExpressions;

namespace AzureMoe.Chat.Web.Services;

/// <summary>A date window for filtering. Bounds are ISO-8601 strings compared
/// lexicographically against <c>Post.date</c> (e.g. "2026-02-01" ≤ date &lt; "2026-03-01").
/// <see cref="Hard"/> means out-of-window chunks are excluded; otherwise the
/// window only boosts in-window chunks.</summary>
public sealed record DateRange(string FromIso, string ToIso, string Label, bool Hard);

/// <summary>The result of analysing a user query without an LLM.</summary>
public sealed record AnalyzedQuery(string Text, DateRange? Date, IReadOnlyList<string> Keywords);

/// <summary>
/// Cheap, LLM-free query understanding: pulls a date window out of Japanese/ISO
/// date expressions and extracts product/entity-ish keywords to seed graph
/// traversal. Deterministic, so it costs nothing on the slow browser CPU.
/// </summary>
public sealed partial class QueryAnalyzer
{
    private readonly Func<DateTime> _now;

    public QueryAnalyzer() : this(() => DateTime.UtcNow) { }
    public QueryAnalyzer(Func<DateTime> now) => _now = now;

    public AnalyzedQuery Analyze(string query)
    {
        var date = ExtractDate(query);
        var keywords = ExtractKeywords(query);
        return new AnalyzedQuery(query, date, keywords);
    }

    // ── Date extraction ────────────────────────────────────────────────────

    private DateRange? ExtractDate(string q)
    {
        // YYYY年M月 / YYYY-MM / YYYY/MM  → that month
        var ym = YearMonth().Match(q);
        if (ym.Success)
        {
            var y = int.Parse(ym.Groups["y"].Value);
            var m = int.Parse(ym.Groups["m"].Value);
            if (m is >= 1 and <= 12) return Month(y, m);
        }

        // YYYY年 (no month) → that year
        var yo = YearOnly().Match(q);
        if (yo.Success)
        {
            var y = int.Parse(yo.Groups["y"].Value);
            return new DateRange($"{y:D4}-01-01", $"{y + 1:D4}-01-01", $"{y}年", Hard: true);
        }

        // 今月 / 先月 / 今年 / 去年・昨年
        var now = _now();
        if (q.Contains("今月")) return Month(now.Year, now.Month);
        if (q.Contains("先月")) { var p = now.AddMonths(-1); return Month(p.Year, p.Month); }
        if (q.Contains("今年")) return new DateRange($"{now.Year:D4}-01-01", $"{now.Year + 1:D4}-01-01", "今年", Hard: true);
        if (q.Contains("去年") || q.Contains("昨年"))
            return new DateRange($"{now.Year - 1:D4}-01-01", $"{now.Year:D4}-01-01", "去年", Hard: true);

        // M月 alone → that month in the current year
        var mo = MonthOnly().Match(q);
        if (mo.Success)
        {
            var m = int.Parse(mo.Groups["m"].Value);
            if (m is >= 1 and <= 12) return Month(now.Year, m);
        }

        // 最近 / 直近 / このごろ → soft preference for the last 90 days
        if (q.Contains("最近") || q.Contains("直近") || q.Contains("このごろ") || q.Contains("この頃"))
        {
            var from = now.AddDays(-90);
            return new DateRange(from.ToString("yyyy-MM-dd"), now.AddDays(1).ToString("yyyy-MM-dd"), "最近", Hard: false);
        }

        return null;
    }

    private static DateRange Month(int y, int m)
    {
        var ny = m == 12 ? y + 1 : y;
        var nm = m == 12 ? 1 : m + 1;
        return new DateRange($"{y:D4}-{m:D2}-01", $"{ny:D4}-{nm:D2}-01", $"{y}年{m}月", Hard: true);
    }

    // ── Keyword extraction ───────────────────────────────────────────────────

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "話題", "最近", "直近", "教えて", "について", "とは", "ニュース", "まとめ", "情報",
        "更新", "アップデート", "新着", "内容", "概要", "どんな", "なに", "ある", "です",
        "ます", "今月", "先月", "今年", "去年", "昨年", "ください", "知りたい", "一覧",
        "the", "and", "for", "what", "について教えて",
    };

    private static IReadOnlyList<string> ExtractKeywords(string q)
    {
        var tokens = new List<string>();
        foreach (Match m in Token().Matches(q))
        {
            var tok = m.Value.Trim();
            if (tok.Length < 2) continue;
            if (Stop.Contains(tok)) continue;
            if (tok.All(char.IsDigit)) continue;            // drop years/numbers
            tokens.Add(tok);
        }
        return tokens.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    // ── Regexes ──────────────────────────────────────────────────────────────

    [GeneratedRegex(@"(?<y>\d{4})\s*[年/\-]\s*(?<m>\d{1,2})\s*月?")]
    private static partial Regex YearMonth();

    [GeneratedRegex(@"(?<y>\d{4})\s*年")]
    private static partial Regex YearOnly();

    [GeneratedRegex(@"(?<![\d])(?<m>\d{1,2})\s*月")]
    private static partial Regex MonthOnly();

    // ASCII words (e.g. "Functions", "Entra", "OpenAI", "GPT-4") and katakana runs.
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9.\-]{1,}|[ァ-ヶー]{2,}")]
    private static partial Regex Token();
}
