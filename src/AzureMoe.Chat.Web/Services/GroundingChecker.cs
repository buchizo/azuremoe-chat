using System.Text;
using System.Text.RegularExpressions;

namespace AzureMoe.Chat.Web.Services;

/// <summary>
/// Deterministic grounding check: every product-name-like ASCII term and every
/// multi-digit number in the answer must also appear in the retrieved context.
/// These are exactly where a small model drifts into its training data (wrong
/// service names, versions, years), and a string lookup catches them reliably
/// in microseconds — unlike asking the same 1.2B model to grade itself OK/NG,
/// which cost a full prefill pass per check and was itself unreliable.
/// </summary>
public static partial class GroundingChecker
{
    // Generic words the model may legitimately use without them being in the
    // context: the brand, and English function words that carry no facts.
    private static readonly HashSet<string> Allow = new(StringComparer.OrdinalIgnoreCase)
    {
        "Azure", "Microsoft",
        "and", "the", "for", "with", "new", "that", "this", "are", "was", "were",
        "has", "have", "from", "into", "not", "now", "all", "can", "will", "its",
    };

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Url();

    // [1] / [1] [2] citations — numbers here are reference ordinals, not facts.
    [GeneratedRegex(@"\[\s*\d+\s*\]")]
    private static partial Regex Citation();

    // Product-ish ASCII terms: "Functions", "AKS", "GPT-4o", "Node.js", "C#".
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+#]*(?:[.\-][A-Za-z0-9+#]+)*")]
    private static partial Regex AsciiTerm();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRun();

    // Dates as a unit — 2026年6月18日 / 2026年6月 / 2026/06/18 / 2026.06.18 /
    // 2026-06 / 6月18日. Checked whole, because each component alone ("2017",
    // "07", "23") is almost always present somewhere in the context, which let
    // an invented "2017.07.23" pass.
    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})\s*年\s*(?<m>\d{1,2})\s*月(?:\s*(?<d>\d{1,2})\s*日)?|(?<!\d)(?<y>(?:19|20)\d{2})[./\-](?<m>\d{1,2})(?:[./\-](?<d>\d{1,2}))?(?!\d)|(?<!\d)(?<m>\d{1,2})\s*月\s*(?<d>\d{1,2})\s*日")]
    private static partial Regex DateExpr();

    /// <summary>Terms in <paramref name="answer"/> that <paramref name="evidence"/>
    /// doesn't contain. Empty means grounded. Pass the user's question along with
    /// the context: restating its terms ("2026年6月の…") is not drift.</summary>
    public static IReadOnlyList<string> FindUnsupported(string answer, string evidence)
    {
        var a   = Clean(answer);
        var ctx = Clean(evidence);
        var ctxLower = ctx.ToLowerInvariant();

        var unsupported = new List<string>();

        // Dates first, compared by value so "2026/06/18" in a reference header
        // supports "2026年6月18日" in the answer. Matched spans are blanked out so
        // their components don't go through the loose per-number check below.
        var ctxDates = new HashSet<string>();
        foreach (Match m in DateExpr().Matches(ctx))
            foreach (var key in DateKeys(m)) ctxDates.Add(key);
        a = DateExpr().Replace(a, m =>
        {
            if (!ctxDates.Contains(DateKeys(m)[0])) unsupported.Add(m.Value.Trim());
            return " ";
        });

        // Remaining numbers by value (versions, counts, years alone).
        var ctxNumbers = DigitRun().Matches(ctx)
            .Select(m => TrimZeros(m.Value)).ToHashSet();

        foreach (Match m in AsciiTerm().Matches(a))
        {
            var term = m.Value;
            if (term.Length < 3 || Allow.Contains(term)) continue;
            // Substring match: "Function" in "Functions", "App" in "Container Apps".
            if (!ctxLower.Contains(term.ToLowerInvariant())) unsupported.Add(term);
        }
        foreach (Match m in DigitRun().Matches(a))
        {
            // Single digits are too common (list counts, "1つ") to be evidence of drift.
            if (m.Value.Length < 2) continue;
            if (!ctxNumbers.Contains(TrimZeros(m.Value))) unsupported.Add(m.Value);
        }
        return unsupported.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Clean(string s) =>
        Citation().Replace(Url().Replace(FoldFullWidth(s), " "), " ");

    // Full-width ASCII (ＡＫＳ, ２０２６, ［１］) → ASCII on both sides. Hand-rolled
    // because string.Normalize(FormKC) is unsupported on browser-wasm.
    private static string FoldFullWidth(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is >= '！' and <= '～' ? (char)(c - 0xFEE0) : c);
        return sb.ToString();
    }

    // Keys a date expression satisfies, most specific first: the answer side
    // needs its exact key ([0]); the evidence side registers every coarser key
    // too, so a context "2026/06/18" supports answers of "2026年6月" or "6月18日".
    private static List<string> DateKeys(Match m)
    {
        var y  = m.Groups["y"].Success ? TrimZeros(m.Groups["y"].Value) : null;
        var mo = TrimZeros(m.Groups["m"].Value);
        var d  = m.Groups["d"].Success ? TrimZeros(m.Groups["d"].Value) : null;
        var keys = new List<string>();
        if (y is not null && d is not null) keys.Add($"{y}-{mo}-{d}");
        if (y is not null) keys.Add($"{y}-{mo}");
        if (d is not null) keys.Add($"--{mo}-{d}");
        return keys;
    }

    private static string TrimZeros(string digits)
    {
        var t = digits.TrimStart('0');
        return t.Length == 0 ? "0" : t;
    }
}
