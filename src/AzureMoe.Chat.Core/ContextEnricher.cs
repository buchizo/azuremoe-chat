using System.Text;

namespace AzureMoe.Chat.Core;

/// <summary>
/// Small-to-big: fills <see cref="Chunk.ContextText"/> with neighbouring text so
/// the browser can hand the LLM richer generation context while the embedding
/// (retrieval key) stays fine-grained over <see cref="Chunk.Text"/> alone.
///
/// - update_item: the service name + sibling bullets of the same (post, H2
///   section), expanded alternately before/after the bullet itself.
/// - prose: the previous and next chunk within the same section.
///
/// Pure and dependency-free so it can be unit-tested against real post HTML.
/// </summary>
public static class ContextEnricher
{
    /// <summary>Char cap for an update_item context (service header + bullets).</summary>
    public const int UpdateItemCap = 1200;

    /// <summary>Char cap for a prose context (prev + self + next).</summary>
    public const int ProseCap = 1500;

    /// <summary>Fills ContextText for every chunk, in place. Chunks whose context
    /// would add nothing beyond their own text keep an empty ContextText (the
    /// browser falls back to Text).</summary>
    public static void Enrich(IReadOnlyList<Chunk> chunks)
    {
        foreach (var group in chunks.GroupBy(c => (c.PostId, c.SectionTitle, c.ChunkType)))
        {
            var ordered = group.OrderBy(c => c.Ordinal).ToList();
            if (group.Key.ChunkType == "update_item")
                EnrichUpdateItems(ordered);
            else
                EnrichProse(ordered);
        }
    }

    private static void EnrichUpdateItems(List<Chunk> siblings)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            var self = siblings[i];
            if (siblings.Count == 1) continue;   // no siblings → Text is already everything

            // The bullet itself is always first so it survives any downstream
            // per-reference truncation; siblings then expand alternately
            // before/after it until the cap.
            var header = string.IsNullOrEmpty(self.ServiceName) ? "" : self.ServiceName + "\n";
            var sb     = new StringBuilder(header);
            sb.Append("- ").Append(self.Text);

            var prev = i - 1;
            var next = i + 1;
            var takePrev = true;
            while (prev >= 0 || next < siblings.Count)
            {
                var idx = takePrev && prev >= 0 ? prev : next < siblings.Count ? next : prev;
                var bullet = "\n- " + siblings[idx].Text;
                if (sb.Length + bullet.Length > UpdateItemCap) break;
                sb.Append(bullet);
                if (idx == prev) prev--; else next++;
                takePrev = !takePrev;
            }

            self.ContextText = sb.ToString();
        }
    }

    private static void EnrichProse(List<Chunk> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            var self = ordered[i];
            if (ordered.Count == 1) continue;

            var sb = new StringBuilder(self.Text);
            if (i > 0 && sb.Length + 2 + ordered[i - 1].Text.Length <= ProseCap)
                sb.Insert(0, ordered[i - 1].Text + "\n\n");
            if (i + 1 < ordered.Count && sb.Length + 2 + ordered[i + 1].Text.Length <= ProseCap)
                sb.Append("\n\n").Append(ordered[i + 1].Text);

            if (sb.Length > self.Text.Length)
                self.ContextText = sb.ToString();
        }
    }
}
