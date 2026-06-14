namespace AzureMoe.Chat.Web.Services;

/// <summary>Knobs that let each retrieval mode tune how wide recall goes.</summary>
public sealed record RetrievalOptions(
    int  FinalTopK      = 5,
    int  VectorTopK     = 18,
    bool UseGraph       = true,
    bool IncludeRelated = false,
    int  ExpansionLimit = 10,
    int  DateOverFetch  = 400);

/// <summary>A chunk collected during recall, with the strongest graph link
/// (shared tags / entities / services) that surfaced it.</summary>
public sealed record Candidate(ChunkResult Chunk, int GraphShared);

/// <summary>
/// Two-phase retrieval:
///   1. <see cref="GatherAsync"/> — wide recall (vector + graph + date) for a
///      search query. Collects candidates; does NOT decide the final order.
///   2. <see cref="RankAndSelectAsync"/> — precision. Re-ranks the candidates by
///      their cosine similarity to the ORIGINAL question (authoritative ranking,
///      identical basis for every candidate regardless of how it was found),
///      drops anything outside the question's relevance pool, and keeps the top
///      few. Graph links are only a small tiebreak, so tangential expansions
///      can't out-rank the chunks that actually answer the question.
/// Splitting the two lets Deep mode pool candidates from several search queries
/// and still rank the union against one question.
/// </summary>
public sealed class RetrievalEngine
{
    private readonly IGraphStore _graph;

    public RetrievalEngine(IGraphStore graph) => _graph = graph;

    // Size of the original-question relevance pool. A candidate must rank within
    // the question's top-AuthorityK (corpus-wide, or within the date window) to
    // survive — this is the precision cutoff.
    private const int    AuthorityK = 100;
    private const double GraphBonus = 0.05;   // tiebreak only
    private const double DateBoost  = 0.05;
    private const int    SharedCap  = 5;

    // ── Phase 1: recall ────────────────────────────────────────────────────
    public async ValueTask<IReadOnlyList<Candidate>> GatherAsync(
        float[] searchVec, AnalyzedQuery q, RetrievalOptions opt, CancellationToken ct = default)
    {
        var order = new List<long>();
        var chunk = new Dictionary<long, ChunkResult>();
        var graph = new Dictionary<long, int>();

        void Add(ChunkResult c, int shared)
        {
            if (c.ChunkId == 0) return;
            if (chunk.TryAdd(c.ChunkId, c)) order.Add(c.ChunkId);
            if (shared > graph.GetValueOrDefault(c.ChunkId)) graph[c.ChunkId] = shared;
        }

        // Base recall.
        if (q.Date is { Hard: true } dr)
        {
            foreach (var c in await _graph.VectorSearchInDateRangeAsync(
                         searchVec, dr.FromIso, dr.ToIso, opt.VectorTopK, opt.DateOverFetch, ct)) Add(c, 0);
            foreach (var c in await _graph.ChunksByDateRangeAsync(dr.FromIso, dr.ToIso, opt.VectorTopK, ct)) Add(c, 0);
        }
        else
        {
            foreach (var c in await _graph.VectorSearchAsync(searchVec, opt.VectorTopK, ct)) Add(c, 0);
        }

        // Graph expansion from the best base hits.
        if (opt.UseGraph)
        {
            var seeds = order.Take(6).ToList();
            foreach (var g in await _graph.ExpandByTagsAsync(seeds, opt.ExpansionLimit, ct))        Add(g.Chunk, g.Shared);
            foreach (var g in await _graph.ExpandByEntitiesAsync(seeds, opt.ExpansionLimit, opt.IncludeRelated, ct)) Add(g.Chunk, g.Shared);
            foreach (var g in await _graph.ExpandByServiceAsync(seeds, opt.ExpansionLimit, ct))     Add(g.Chunk, g.Shared);
            if (q.Keywords.Count > 0)
                foreach (var g in await _graph.SearchByKeywordsAsync(q.Keywords, opt.ExpansionLimit, ct)) Add(g.Chunk, g.Shared);
        }

        return order.Select(id => new Candidate(chunk[id], graph.GetValueOrDefault(id))).ToList();
    }

    // ── Phase 2: precision rank + cutoff against the ORIGINAL question ───────
    public async ValueTask<IReadOnlyList<ChunkResult>> RankAndSelectAsync(
        IReadOnlyList<Candidate> candidates, float[] origVec, AnalyzedQuery origQ,
        RetrievalOptions opt, CancellationToken ct = default)
    {
        var hard = origQ.Date is { Hard: true };

        // Authority ranking: real cosine of each chunk to the original question
        // (whole corpus, or within the date window for a dated question).
        IReadOnlyList<ChunkResult> authority = hard && origQ.Date is { } dr
            ? await _graph.VectorSearchInDateRangeAsync(origVec, dr.FromIso, dr.ToIso, AuthorityK, Math.Max(opt.DateOverFetch, 600), ct)
            : await _graph.VectorSearchAsync(origVec, AuthorityK, ct);

        var rel = new Dictionary<long, double>();
        foreach (var a in authority) rel[a.ChunkId] = 1.0 - a.Distance;

        // Pool = recall candidates ∪ the authority list itself (so the question's
        // own top hits are always considered, even if recall used a rewrite).
        var pool = new Dictionary<long, (ChunkResult Chunk, int Shared)>();
        foreach (var c in candidates) pool[c.Chunk.ChunkId] = (c.Chunk, c.GraphShared);
        foreach (var a in authority)  pool.TryAdd(a.ChunkId, (a, 0));

        var scored = new List<(ChunkResult Chunk, double Rel, double Score)>();
        foreach (var (c, shared) in pool.Values)
        {
            if (origQ.Date is { } d && d.Hard && !InRange(c.Date, d))
                continue;                                          // hard date filter

            if (!rel.TryGetValue(c.ChunkId, out var r))
            {
                if (!hard) continue;                               // non-date: outside the relevance pool → drop (precision)
                r = 0.0;                                           // dated: keep for coverage, lowest priority
            }

            var score = r + GraphBonus * (Math.Min(shared, SharedCap) / (double)SharedCap);
            if (origQ.Date is { Hard: false } soft && InRange(c.Date, soft)) score += DateBoost;
            scored.Add((c, r, score));
        }

        // Never starve generation: if nothing passed, keep the best recall candidates.
        if (scored.Count == 0)
            scored = candidates.Take(opt.FinalTopK).Select(c => (c.Chunk, 0.5, 0.5)).ToList();

        return scored
            .OrderByDescending(s => s.Score)
            .Take(opt.FinalTopK)
            .Select(s => s.Chunk with { Distance = Math.Clamp(1.0 - s.Rel, 0.0, 1.0) })
            .ToList();
    }

    private static bool InRange(string date, DateRange r) =>
        !string.IsNullOrEmpty(date)
        && string.CompareOrdinal(date, r.FromIso) >= 0
        && string.CompareOrdinal(date, r.ToIso) < 0;
}
