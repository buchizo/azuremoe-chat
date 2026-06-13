using System.Net.Http.Json;
using System.Text.Json;
using AzureMoe.Chat.Core;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Reads published posts from the WordPress REST API
/// (<c>/wp-json/wp/v2/posts</c>), following pagination and resolving tag names
/// via the embedded <c>_embed</c> term data.
/// </summary>
public sealed class WordPressClient(HttpClient http, string siteUrl)
{
    private readonly string _base = siteUrl.TrimEnd('/');

    public async Task<IReadOnlyList<Post>> FetchPostsAsync(int maxPosts, CancellationToken ct = default)
    {
        var posts = new List<Post>();
        const int perPage = 50;
        for (var page = 1; ; page++)
        {
            var url = $"{_base}/wp-json/wp/v2/posts?per_page={perPage}&page={page}&_embed=wp:term&orderby=modified&order=desc";
            using var resp = await http.GetAsync(url, ct);
            // WP returns 400 with code rest_post_invalid_page_number once you page past the end.
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest) break;
            resp.EnsureSuccessStatusCode();

            var batch = await resp.Content.ReadFromJsonAsync<List<WpPost>>(cancellationToken: ct) ?? [];
            if (batch.Count == 0) break;

            foreach (var p in batch)
            {
                posts.Add(new Post(
                    Id: p.Id,
                    Title: System.Net.WebUtility.HtmlDecode(p.Title?.Rendered ?? string.Empty),
                    Url: p.Link ?? string.Empty,
                    Date: p.Modified ?? p.Date ?? string.Empty,
                    Html: p.Content?.Rendered ?? string.Empty,
                    Tags: ExtractTagNames(p)));
                if (maxPosts > 0 && posts.Count >= maxPosts) return posts;
            }

            if (batch.Count < perPage) break;
        }
        return posts;
    }

    private static List<string> ExtractTagNames(WpPost p)
    {
        // _embedded["wp:term"] is an array of term groups (categories, tags, …).
        var names = new List<string>();
        if (p.Embedded?.Terms is { } groups)
            foreach (var group in groups)
                foreach (var term in group)
                    if (!string.IsNullOrWhiteSpace(term.Name))
                        names.Add(System.Net.WebUtility.HtmlDecode(term.Name));
        return names;
    }

    // --- DTOs for the slice of the WP schema we use ---
    private sealed record WpPost
    {
        public long Id { get; init; }
        public string? Link { get; init; }
        public string? Date { get; init; }
        public string? Modified { get; init; }
        public WpRendered? Title { get; init; }
        public WpRendered? Content { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("_embedded")]
        public Embedded? Embedded { get; init; }
    }

    private sealed record WpRendered
    {
        [System.Text.Json.Serialization.JsonPropertyName("rendered")]
        public string? Rendered { get; init; }
    }

    private sealed record Embedded
    {
        [System.Text.Json.Serialization.JsonPropertyName("wp:term")]
        public List<List<Term>>? Terms { get; init; }
    }

    private sealed record Term
    {
        public string? Name { get; init; }
        public string? Taxonomy { get; init; }
    }
}
