using System.Text;
using System.Text.RegularExpressions;

namespace AzureMoe.Chat.Core;

/// <summary>
/// Turns post HTML into clean, chunked plain text. Pure and dependency-free so
/// it can be unit-tested and (if ever needed) reused in the browser. HTML
/// parsing here is deliberately lightweight regex stripping — the ingest tool
/// uses HtmlAgilityPack upstream for the heavy lifting and passes plain text in,
/// but this also copes with raw HTML directly.
/// </summary>
public static partial class Chunking
{
    /// <summary>Target chunk size in characters. Japanese runs ~0.6-0.9 tokens/char
    /// under XLM-R SentencePiece, and the embed input also carries a title/service
    /// prefix — 600 chars keeps everything inside E5's 512-token window. Generation
    /// context no longer depends on chunk size (see ContextEnricher), so smaller
    /// retrieval keys are strictly better.</summary>
    public const int TargetChars = 600;

    /// <summary>Hard ceiling — a single paragraph longer than this is force-split.</summary>
    public const int MaxChars = 900;

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"<h[23][^>]*>(.*?)</h[23]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex H2H3();

    // STX (U+0002) — a control character that never appears in WordPress blog HTML.
    // Used as a section-heading marker that survives HtmlToText's tag-stripping pass.
    private const char SectionMarker = '\x02';

    [GeneratedRegex(@"[ \t\f\v\r]+")]
    private static partial Regex InlineWhitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    /// <summary>Strip HTML to readable plain text, preserving paragraph breaks.</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var s = ScriptStyle().Replace(html, " ");
        // Turn block-level boundaries into newlines before dropping tags.
        s = Regex.Replace(s, @"</(p|div|li|h[1-6]|br|tr)\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Tags().Replace(s, string.Empty);
        s = System.Net.WebUtility.HtmlDecode(s);
        s = InlineWhitespace().Replace(s, " ");
        s = BlankRuns().Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>
    /// Article-post variant: strips HTML, tracks H2/H3 headings, and splits into
    /// chunks at paragraph boundaries (same size limits as <see cref="SplitIntoChunks"/>).
    /// Each returned tuple carries the chunk text and the heading that preceded it.
    /// </summary>
    public static IReadOnlyList<(string Text, string SectionTitle)> SplitWithSections(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        // Replace <h2>/<h3> elements with §heading§ markers before tag stripping.
        var marked = H2H3().Replace(html, m =>
        {
            var inner   = Tags().Replace(m.Groups[1].Value, "");
            var heading = System.Net.WebUtility.HtmlDecode(inner).Trim();
            return $"\n{SectionMarker}{heading}{SectionMarker}\n";
        });

        var text  = HtmlToText(marked);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results        = new List<(string Text, string SectionTitle)>();
        var current        = new StringBuilder();
        var currentSection = "";

        void Flush()
        {
            if (current.Length == 0) return;
            var t = current.ToString().Trim();
            if (t.Length > 0) results.Add((t, currentSection));
            current.Clear();
        }

        foreach (var line in lines)
        {
            if (line.Length > 2 && line[0] == SectionMarker && line[^1] == SectionMarker)
            {
                Flush();
                currentSection = line[1..^1].Trim();
                continue;
            }

            foreach (var piece in HardSplit(line))
            {
                if (current.Length > 0 && current.Length + piece.Length + 1 > TargetChars)
                    Flush();
                if (current.Length > 0) current.Append('\n');
                current.Append(piece);
            }
        }

        Flush();
        return results;
    }

    /// <summary>
    /// Split text into chunks at paragraph boundaries, accumulating paragraphs
    /// up to <see cref="TargetChars"/> and force-splitting any single paragraph
    /// that exceeds <see cref="MaxChars"/>.
    /// </summary>
    public static IReadOnlyList<string> SplitIntoChunks(string text)
    {
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chunks = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0) { chunks.Add(current.ToString().Trim()); current.Clear(); }
        }

        foreach (var para in paragraphs)
        {
            foreach (var piece in HardSplit(para))
            {
                if (current.Length > 0 && current.Length + piece.Length + 1 > TargetChars)
                    Flush();
                if (current.Length > 0) current.Append('\n');
                current.Append(piece);
            }
        }
        Flush();
        return chunks;
    }

    /// <summary>Force-split a paragraph longer than MaxChars on sentence
    /// punctuation (Japanese 。！？ and ASCII .!?), falling back to a hard cut.</summary>
    private static IEnumerable<string> HardSplit(string para)
    {
        if (para.Length <= MaxChars) { yield return para; yield break; }

        var sentences = Regex.Split(para, @"(?<=[。！？\.!?])");
        var buf = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (buf.Length > 0 && buf.Length + sentence.Length > MaxChars)
            {
                yield return buf.ToString();
                buf.Clear();
            }
            if (sentence.Length > MaxChars)
            {
                // pathological single sentence — cut on a character boundary
                for (var i = 0; i < sentence.Length; i += MaxChars)
                    yield return sentence.Substring(i, Math.Min(MaxChars, sentence.Length - i));
            }
            else
            {
                buf.Append(sentence);
            }
        }
        if (buf.Length > 0) yield return buf.ToString();
    }
}
