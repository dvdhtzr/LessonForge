// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;

namespace LessonForge.Core.Rag;

/// <summary>
/// Enforce citation provenance at the stage boundaries. This checks that a URL
/// belongs to evidence actually retrieved for the run; it deliberately does not
/// pretend that URL membership proves factual support for a particular statement.
/// </summary>
public static class SourceCitationValidator
{
    public static void RequireRetrieved(
        IEnumerable<CitationRef> references, IEnumerable<Citation> retrieved, string context)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(retrieved);
        var known = retrieved
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var index = 0;
        foreach (var citation in references)
        {
            if (citation is null || string.IsNullOrWhiteSpace(citation.Url) ||
                !known.TryGetValue(citation.Url, out var source))
                throw new InvalidOperationException(
                    $"{context} citation[{index}] is not present in this run's rights-eligible source inventory.");

            // Source titles are host-controlled, not a model's guess about authorship.
            citation.Title = source.Title;
            citation.Url = source.Url;
            index++;
        }
    }
}
