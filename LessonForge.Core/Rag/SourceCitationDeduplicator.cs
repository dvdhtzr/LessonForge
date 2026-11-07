// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;

namespace LessonForge.Core.Rag;

/// <summary>
/// Keep one link per distinct source document in each independent citation list.
/// This is presentation/provenance hygiene, NOT evidence validation: callers
/// validate citations against the retrieval index before normalizing them.
/// A duplicate is defined by URL, never by a title that different documents
/// may share. Preserve the first (already validated) title, URL and order.
/// </summary>
public static class SourceCitationDeduplicator
{
    /// <summary>
    /// Identify the same HTTP(S) document despite hostname casing or a
    /// fragment link to a section. Preserve path case, query and nondefault
    /// ports so distinct documents are never accidentally grouped.
    /// </summary>
    public static string UrlKey(string? url)
    {
        var text = url?.Trim() ?? "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return text;

        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}" +
               $"{port}{uri.AbsolutePath}{uri.Query}";
    }

    public static List<CitationRef> Unique(IEnumerable<CitationRef> citations)
    {
        ArgumentNullException.ThrowIfNull(citations);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CitationRef>();
        foreach (var citation in citations)
        {
            // Leave invalid entries in place for the existing strict validators;
            // removing them would conceal problems with model/educator output.
            if (citation is null || string.IsNullOrWhiteSpace(citation.Url) ||
                seen.Add(UrlKey(citation.Url)))
                result.Add(citation!);
        }
        return result;
    }

    public static void NormalizeInPlace(List<CitationRef>? citations)
    {
        if (citations is null) return;
        var unique = Unique(citations);
        citations.Clear();
        citations.AddRange(unique);
    }

    public static void Normalize(ResearchOutput research)
    {
        ArgumentNullException.ThrowIfNull(research);
        NormalizeInPlace(research.SourceInventory);
        foreach (var fact in research.GroundedFacts ?? [])
            if (fact is not null)
                NormalizeInPlace(fact.Citations);
        if (research.Perspectives is not null)
            Normalize(research.Perspectives);
    }

    public static void Normalize(PerspectiveAnalysisOutput analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        foreach (var view in analysis.Perspectives ?? [])
            if (view is not null)
                NormalizeInPlace(view.Citations);
    }

    public static void Normalize(LessonPlanOutput plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        NormalizeInPlace(plan.Citations);
    }

    public static void Normalize(QuizOutput quiz)
    {
        ArgumentNullException.ThrowIfNull(quiz);
        foreach (var question in quiz.Questions ?? [])
            if (question is not null)
                NormalizeInPlace(question.Citations);
    }

    /// <summary>Also clean saved lessons created before this rule existed.</summary>
    public static void Normalize(VerifiedLessonOutput result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Plan is not null) Normalize(result.Plan);
        if (result.Quiz is not null) Normalize(result.Quiz);
        if (result.Perspectives is not null) Normalize(result.Perspectives);

        if (result.SourceRecords is { Count: > 0 })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            result.SourceRecords = result.SourceRecords
                .Where(source => source is null ||
                    string.IsNullOrWhiteSpace(source.Url) ||
                    seen.Add(UrlKey(source.Url)))
                .ToList();
        }
    }
}
