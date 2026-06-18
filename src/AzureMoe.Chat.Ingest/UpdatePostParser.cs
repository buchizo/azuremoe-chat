using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Parses "Azure Update(s)" blog posts by HTML structure rather than character count.
///
/// Structure assumed:
///   &lt;h2&gt;Azure Functions&lt;/h2&gt;       ← service name
///   &lt;ul&gt;
///     &lt;li&gt;Update description       ← one update item
///       &lt;ul&gt;&lt;li&gt;sub-detail&lt;/li&gt;&lt;/ul&gt;  ← child bullets stay with parent
///     &lt;/li&gt;
///   &lt;/ul&gt;
///
/// Returns (ServiceName, Text, ChunkType) per chunk where ChunkType is
/// "update_item" for list items and "prose" for paragraphs outside lists.
/// </summary>
public static class UpdatePostParser
{
    public static IReadOnlyList<(string ServiceName, string Text, string ChunkType)> Parse(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<(string ServiceName, string Text, string ChunkType)>();
        var body    = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;

        var currentService = "";
        var pendingProse   = new StringBuilder();

        void FlushProse()
        {
            var text = pendingProse.ToString().Trim();
            pendingProse.Clear();
            if (!string.IsNullOrWhiteSpace(text))
                results.Add((currentService, text, "prose"));
        }

        foreach (var node in body.ChildNodes)
        {
            if (node.NodeType == HtmlNodeType.Text) continue;

            switch (node.Name.ToLowerInvariant())
            {
                case "h2":
                    FlushProse();
                    currentService = CleanText(node);
                    break;

                case "h3":
                case "h4":
                    var sub = CleanText(node);
                    if (!string.IsNullOrWhiteSpace(sub))
                        pendingProse.Append(sub).Append('\n');
                    break;

                case "ul":
                case "ol":
                    FlushProse();
                    foreach (var li in node.ChildNodes.Where(n => n.Name == "li"))
                    {
                        var text = ExtractLiText(li);
                        if (!string.IsNullOrWhiteSpace(text))
                            results.Add((currentService, text, "update_item"));
                    }
                    break;

                case "p":
                case "div":
                case "blockquote":
                    var prose = CleanText(node);
                    if (!string.IsNullOrWhiteSpace(prose))
                        pendingProse.Append(prose).Append('\n');
                    break;
            }
        }

        FlushProse();
        return results;
    }

    private static string CleanText(HtmlNode node) =>
        HtmlEntity.DeEntitize(
            Regex.Replace(node.InnerText ?? "", @"\s+", " ")).Trim();

    private static string ExtractLiText(HtmlNode li)
    {
        var sb = new StringBuilder();

        foreach (var child in li.ChildNodes)
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Text:
                    var t = HtmlEntity.DeEntitize(
                        Regex.Replace(child.InnerText, @"\s+", " ")).Trim();
                    if (!string.IsNullOrWhiteSpace(t))
                        sb.Append(t);
                    break;

                case HtmlNodeType.Element when child.Name is "ul" or "ol":
                    // Child bullets are related comments — keep with parent, indented.
                    foreach (var nested in child.ChildNodes.Where(n => n.Name == "li"))
                    {
                        var nt = HtmlEntity.DeEntitize(
                            Regex.Replace(nested.InnerText, @"\s+", " ")).Trim();
                        if (!string.IsNullOrWhiteSpace(nt))
                            sb.Append("\n  - ").Append(nt);
                    }
                    break;

                case HtmlNodeType.Element:
                    var et = HtmlEntity.DeEntitize(
                        Regex.Replace(child.InnerText ?? string.Empty, @"\s+", " ")).Trim();
                    if (!string.IsNullOrWhiteSpace(et))
                        sb.Append(et);
                    break;
            }
        }

        return sb.ToString().Trim();
    }
}
