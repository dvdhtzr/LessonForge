// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using LessonForge.Core.Dto;
using LessonForge.Core.Rag;

namespace LessonForge.Core.Agents;

/// <summary>Helpers for turning retrieved chunks into grounded prompt context and citations.</summary>
internal static class RagPrompt
{
    /// <summary>
    /// Runs several searches against the session and returns a de-duplicated, numbered evidence
    /// block suitable for pasting into an agent prompt. Each snippet is labelled with its source so
    /// the model can cite it by URL.
    /// </summary>
    public static async Task<string> GatherEvidenceAsync(
        RetrievalSession session, IEnumerable<string> queries, int perQuery = 5, int maxSnippets = 18,
        CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var picked = new List<ScoredChunk>();

        foreach (var query in queries)
        {
            foreach (var scored in await session.SearchAsync(query, perQuery, ct))
            {
                if (seen.Add(scored.Chunk.Id))
                    picked.Add(scored);
            }
        }

        var top = picked.OrderByDescending(p => p.Score).Take(maxSnippets).ToList();

        var sb = new StringBuilder();
        for (var i = 0; i < top.Count; i++)
        {
            var c = top[i].Chunk;
            sb.AppendLine($"[{i + 1}] SOURCE: {c.SourceTitle} — {c.SourceUrl}");
            sb.AppendLine(Trim(c.Text, 1200));
            sb.AppendLine();
        }

        return sb.Length == 0 ? "(no sources retrieved)" : sb.ToString();
    }

    /// <summary>The session's distinct sources as DTO citations, for seeding agent output.</summary>
    public static List<CitationRef> InventoryCitations(RetrievalSession session)
        => session.Citations.Select(c => new CitationRef { Title = c.Title, Url = c.Url }).ToList();

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
