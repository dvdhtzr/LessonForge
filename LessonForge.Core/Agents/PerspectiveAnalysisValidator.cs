// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>
/// Deterministic guardrails for the optional perspective-analysis contract.
/// Provider JSON schema checks structure; this boundary checks meaning and provenance.
/// </summary>
public static class PerspectiveAnalysisValidator
{
    public const string Supported = "supported_disagreement";
    public const string NoDisagreement = "no_substantive_disagreement";
    public const string Insufficient = "insufficient_evidence";

    public static void Validate(PerspectiveAnalysisOutput analysis, IEnumerable<CitationRef> knownSources)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(knownSources);

        if (analysis.Status is not (Supported or NoDisagreement or Insufficient))
            throw new InvalidOperationException(
                "PerspectiveAnalysis returned an invalid status; expected supported_disagreement, " +
                "no_substantive_disagreement or insufficient_evidence.");

        if (analysis.Perspectives is null)
            throw new InvalidOperationException("PerspectiveAnalysis returned no perspectives array.");

        if (string.IsNullOrWhiteSpace(analysis.EvidenceLimitations))
            throw new InvalidOperationException(
                "PerspectiveAnalysis must explain evidentiary limitations, even when a disagreement is supported.");

        if (analysis.Status == Supported && analysis.Perspectives.Count is < 2 or > 3)
            throw new InvalidOperationException(
                "PerspectiveAnalysis must supply 2-3 substantiated interpretations when disagreement is supported.");

        if (analysis.Status != Supported && analysis.Perspectives.Count != 0)
            throw new InvalidOperationException(
                "PerspectiveAnalysis must not introduce competing viewpoints when evidence does not establish disagreement.");

        var allowed = knownSources
            .Where(source => source is not null && !string.IsNullOrWhiteSpace(source.Url))
            .Select(source => source.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < analysis.Perspectives.Count; i++)
        {
            var view = analysis.Perspectives[i];
            if (view is null ||
                string.IsNullOrWhiteSpace(view.Name) ||
                string.IsNullOrWhiteSpace(view.Interpretation) ||
                string.IsNullOrWhiteSpace(view.EvidenceBasis) ||
                string.IsNullOrWhiteSpace(view.Limitations) ||
                view.Citations is not { Count: > 0 })
                throw new InvalidOperationException(
                    $"PerspectiveAnalysis perspectives[{i}] omitted required interpretation or evidence fields.");

            foreach (var citation in view.Citations)
            {
                if (citation is null ||
                    string.IsNullOrWhiteSpace(citation.Title) ||
                    !allowed.Contains(citation.Url))
                    throw new InvalidOperationException(
                        $"PerspectiveAnalysis perspectives[{i}] references a source outside the retrieved evidence.");
            }
        }

        if (analysis.Perspectives.Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != analysis.Perspectives.Count)
            throw new InvalidOperationException(
                "PerspectiveAnalysis returned duplicate perspectives.");

        if (analysis.AreasOfAgreement is null || analysis.DiscussionQuestions is null ||
            analysis.AreasOfAgreement.Any(string.IsNullOrWhiteSpace) ||
            analysis.DiscussionQuestions.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "PerspectiveAnalysis must supply valid agreement and discussion-question arrays.");
    }
}
