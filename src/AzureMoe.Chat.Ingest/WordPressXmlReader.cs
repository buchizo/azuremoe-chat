using System.Globalization;
using System.Xml.Linq;
using AzureMoe.Chat.Core;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Reads posts from WordPress eXtended RSS (WXR) export files.
/// Supports WXR 1.0–1.2 and WordPress.com export format.
/// </summary>
public static class WordPressXmlReader
{
    public static IReadOnlyList<Post> ReadFromDirectory(
        string xmlDir, int maxPosts = 0, Action<string>? log = null)
    {
        log ??= _ => { };

        if (!Directory.Exists(xmlDir))
            throw new DirectoryNotFoundException(
                $"XML ディレクトリが見つかりません: {Path.GetFullPath(xmlDir)}\n" +
                $"WordPress 管理画面 → ツール → エクスポート で XML を取得し {xmlDir}/ に配置してください。");

        var xmlFiles = Directory.GetFiles(xmlDir, "*.xml", SearchOption.TopDirectoryOnly);
        if (xmlFiles.Length == 0)
            throw new FileNotFoundException(
                $"*.xml が {xmlDir}/ に見つかりません。WordPress エクスポートファイルを配置してください。");

        var allPosts  = new Dictionary<long, Post>();
        long fallback = 0;

        foreach (var path in xmlFiles.OrderBy(f => f))
        {
            log($"  {Path.GetFileName(path)} を解析中...");
            var filePosts = ParseFile(path, fallback, out var next, log);
            log($"    取込: {filePosts.Count} 件");
            foreach (var p in filePosts)
                allPosts.TryAdd(p.Id, p);   // first-wins on duplicate ids across files
            fallback = next;
        }

        IEnumerable<Post> ordered = allPosts.Values.OrderByDescending(p => p.Date);
        if (maxPosts > 0) ordered = ordered.Take(maxPosts);
        return ordered.ToList();
    }

    // -----------------------------------------------------------------------
    private static List<Post> ParseFile(
        string path, long fallbackId, out long nextFallbackId, Action<string> log)
    {
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (Exception ex)
        {
            throw new InvalidDataException($"XML 解析失敗 ({Path.GetFileName(path)}): {ex.Message}", ex);
        }

        var wpNs      = DetectWpNamespace(doc, log);
        var contentNs = DetectContentNamespace(doc, log);
        nextFallbackId = fallbackId;

        // <rss><channel> または <feed> など構造が違う場合に備える
        var channel = doc.Root?.Element("channel") ?? doc.Root;
        if (channel == null)
        {
            log($"    [エラー] ルート要素が null — ファイルが壊れている可能性があります");
            return [];
        }

        var items = channel.Elements("item").ToList();
        log($"    <item> 要素数: {items.Count}、wp名前空間: {(wpNs == XNamespace.None ? "(検出不可)" : wpNs.NamespaceName)}");

        if (items.Count == 0)
        {
            // 構造診断: channel 直下の要素名を表示
            var childNames = channel.Elements()
                .Select(e => e.Name.LocalName)
                .Distinct()
                .Take(20);
            log($"    [診断] channel 直下の要素: {string.Join(", ", childNames)}");
            return [];
        }

        var results     = new List<Post>();
        var skipCounts  = new Dictionary<string, int>();

        foreach (var item in items)
        {
            // --- post_type: 要素が存在しない場合は "post" とみなす ---
            var postType = GetWpValue(item, wpNs, "post_type");
            if (postType != null && postType != "post")
            {
                skipCounts[$"type={postType}"] = skipCounts.GetValueOrDefault($"type={postType}") + 1;
                continue;
            }

            // --- status: 要素が存在しない場合は "publish" とみなす ---
            var status = GetWpValue(item, wpNs, "status");
            if (status != null && status != "publish")
            {
                skipCounts[$"status={status}"] = skipCounts.GetValueOrDefault($"status={status}") + 1;
                continue;
            }

            var idStr = GetWpValue(item, wpNs, "post_id") ?? "";
            var id    = long.TryParse(idStr, out var parsed) && parsed > 0 ? parsed : ++nextFallbackId;

            var title = System.Net.WebUtility.HtmlDecode(
                item.Element("title")?.Value?.Trim() ?? "");

            // <link> は RSS 標準要素 (名前空間なし)
            var link = item.Element("link")?.Value?.Trim() ?? "";

            // 日付: GMT を優先、なければローカル
            var rawDate = GetWpValue(item, wpNs, "post_date_gmt")
                       ?? GetWpValue(item, wpNs, "post_date")
                       ?? "";
            var date = NormalizeDate(rawDate);

            // 本文 HTML: <content:encoded>
            var html = item.Element(contentNs + "encoded")?.Value ?? "";

            // タグ
            var tags = item.Elements("category")
                .Where(c => (c.Attribute("domain")?.Value ?? "") == "post_tag")
                .Select(c => c.Value.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            results.Add(new Post(id, title, link, date, html, tags));
        }

        if (skipCounts.Count > 0)
            log($"    スキップ: {string.Join(", ", skipCounts.Select(kv => $"{kv.Key} ×{kv.Value}"))}");

        return results;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// wp:XXX の値を返す。名前空間が検出できなかった場合のフォールバックとして
    /// ローカル名だけで検索する。
    /// </summary>
    private static string? GetWpValue(XElement item, XNamespace wpNs, string localName)
    {
        // 検出された名前空間で検索
        var val = item.Element(wpNs + localName)?.Value?.Trim();
        if (val != null) return val;

        // フォールバック: ローカル名だけで全子孫を検索
        return item.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == localName)
            ?.Value?.Trim();
    }

    private static XNamespace DetectWpNamespace(XDocument doc, Action<string>? log)
    {
        // 1. root 要素の属性宣言から探す
        if (doc.Root != null)
            foreach (var attr in doc.Root.Attributes())
                if (attr.IsNamespaceDeclaration &&
                    attr.Value.Contains("wordpress.org/export"))
                    return XNamespace.Get(attr.Value);

        // 2. 実際の要素名の名前空間から推測
        foreach (var el in doc.Descendants())
            if (el.Name.NamespaceName.Contains("wordpress.org"))
                return XNamespace.Get(el.Name.NamespaceName);

        // 3. "wp:" プレフィックスを持つ要素を探す (名前空間宣言がない壊れたXMLへの対応)
        foreach (var el in doc.Descendants())
            if (el.Name.LocalName is "post_type" or "status" or "post_id" or "post_date")
                if (el.Name.Namespace != XNamespace.None)
                    return el.Name.Namespace;

        log?.Invoke("    [警告] wp: 名前空間を検出できませんでした。ローカル名フォールバックを使用します。");
        return XNamespace.None;   // GetWpValue のフォールバックパスが使われる
    }

    private static XNamespace DetectContentNamespace(XDocument doc, Action<string>? log)
    {
        if (doc.Root != null)
            foreach (var attr in doc.Root.Attributes())
                if (attr.IsNamespaceDeclaration &&
                    attr.Value.Contains("purl.org/rss/1.0/modules/content"))
                    return XNamespace.Get(attr.Value);

        foreach (var el in doc.Descendants())
            if (el.Name.LocalName == "encoded" &&
                el.Name.Namespace != XNamespace.None)
                return el.Name.Namespace;

        return XNamespace.Get("http://purl.org/rss/1.0/modules/content/");
    }

    private static string NormalizeDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        raw = raw.Trim();

        // "2025-01-01 09:00:00" → "2025-01-01T09:00:00Z"
        if (raw.Length == 19 && raw[10] == ' ')
            return raw[..10] + "T" + raw[11..] + "Z";

        // Already ISO with 'T': keep, but append 'Z' when no timezone is present.
        if (raw.Length >= 19 && raw[10] == 'T')
        {
            var hasTz = raw.EndsWith('Z') || raw.Contains('+') || raw.LastIndexOf('-') > 10;
            return hasTz ? raw : raw + "Z";
        }

        // Date only "2025-01-01"
        if (raw.Length == 10 && raw[4] == '-' && raw[7] == '-')
            return raw + "T00:00:00Z";

        // Anything else: parse and normalise to UTC ISO-8601.
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

        return raw;
    }
}
