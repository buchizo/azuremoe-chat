using System.Text.RegularExpressions;
using HtmlAgilityPack;
using AzureMoe.Chat.Core;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Fetches a single WordPress blog post from a URL and converts it into a Post record.
/// </summary>
public static class BlogPostFetcher
{
    public static async Task<Post> FetchAsync(string url, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AzureMoeIngest/1.0)");

        var html = await http.GetStringAsync(url, ct);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Post ID: body class "postid-12345"
        var bodyClass = doc.DocumentNode.SelectSingleNode("//body")?.GetAttributeValue("class", "") ?? "";
        var postIdMatch = Regex.Match(bodyClass, @"postid-(\d+)");
        long postId = postIdMatch.Success
            ? long.Parse(postIdMatch.Groups[1].Value)
            : (long)(uint)url.GetHashCode();   // stable fallback; 0 is reassigned by GetNextIds

        // Title: h1.entry-title, or page <title> stripped of site suffix
        var titleNode = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'entry-title')]");
        string title;
        if (titleNode != null)
        {
            title = HtmlEntity.DeEntitize(titleNode.InnerText.Trim());
        }
        else
        {
            var pageTitle = doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "";
            var pipeIdx = pageTitle.LastIndexOf(" | ", StringComparison.Ordinal);
            title = HtmlEntity.DeEntitize(pipeIdx >= 0 ? pageTitle[..pipeIdx].Trim() : pageTitle.Trim());
        }

        // Date: <time class="entry-date" datetime="2026-06-18T09:00:00+09:00">
        var timeNode = doc.DocumentNode.SelectSingleNode("//time[contains(@class,'entry-date')]");
        var rawDate = timeNode?.GetAttributeValue("datetime", "") ?? "";
        var date = NormalizeDate(rawDate, url);

        // Content: .entry-content, fallback to article, then main
        var contentNode = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'entry-content')]")
                       ?? doc.DocumentNode.SelectSingleNode("//article")
                       ?? doc.DocumentNode.SelectSingleNode("//main");
        var contentHtml = contentNode?.InnerHtml ?? "";

        // Tags: <a rel="tag">
        var tagNodes = doc.DocumentNode.SelectNodes("//a[@rel='tag']");
        var tags = tagNodes is null
            ? new List<string>()
            : tagNodes
                .Select(n => HtmlEntity.DeEntitize(n.InnerText.Trim()))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidDataException($"タイトルを取得できませんでした: {url}");
        if (string.IsNullOrWhiteSpace(contentHtml))
            throw new InvalidDataException($"本文コンテンツを取得できませんでした: {url}");

        return new Post(postId, title, url, date, contentHtml, tags);
    }

    private static string NormalizeDate(string raw, string fallbackUrl)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            DateTimeOffset.TryParse(raw, out var dto))
            return dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Extract date from URL path: /2026/06/18/
        var m = Regex.Match(fallbackUrl, @"/(\d{4})/(\d{2})/(\d{2})/");
        if (m.Success)
            return $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}T00:00:00Z";

        return raw;
    }
}
