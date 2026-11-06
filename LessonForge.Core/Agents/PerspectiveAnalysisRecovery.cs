// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>
/// Bounded recovery for incomplete local-model perspective comparisons.
/// Never repairs or accepts an invalid viewpoint: retry the entire small
/// response once, then return an explicitly inconclusive *review status*.
/// This distinguishes the research that was retrieved from an analysis that
/// the model failed to substantiate. Transport, parse, or generation failures
/// are intentionally NOT caught by this policy.
/// </summary>
public static class PerspectiveAnalysisRecovery
{
    public const int MaxAttempts = 2;

    /// <summary>Instructions shared by the normal prompt and recovery pass.</summary>
    public const string OutputDiscipline = """
        Return exactly TWO perspectives if status is supported_disagreement.
        EVERY perspective MUST have all FIVE fields:
          name: nonempty short label;
          interpretation: nonempty short explanation;
          evidence_basis: nonempty specific evidence from the supplied excerpts;
          limitations: nonempty relevant weakness or qualification;
          citations: NONEMPTY array of {title, url} copied from shown SOURCE excerpts.
        Keep each string to ONE short sentence and each perspective to ONE source
        citation when sufficient. Every URL must appear in the supplied evidence.
        Never use empty strings, empty citations, or invented references.
        If even ONE of these fields cannot be supported for EITHER viewpoint,
        return status="insufficient_evidence" with perspectives=[] instead.
        For insufficient_evidence, include a nonempty evidence_limitations and
        arrays for areas_of_agreement and discussion_questions (empty is valid).
        Do not guess missing evidence or claim that two views are substantiated
        simply because they are commonly discussed.
        """;

    /// <summary>
    /// The generator runs once normally and once with tighter instructions if
    /// the deterministic semantic/URL validator rejects its answer.
    /// </summary>
    public static async Task<PerspectiveAnalysisOutput> RunAsync(
        Func<int, CancellationToken, Task<PerspectiveAnalysisOutput>> generate,
        IEnumerable<CitationRef> retrievedSources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generate);
        ArgumentNullException.ThrowIfNull(retrievedSources);
        var allowed = retrievedSources.ToList();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Deserialization/transport exceptions propagate. Only a fully
            // deserialized object rejected by semantic validation is retried.
            var result = await generate(attempt, cancellationToken);
            try
            {
                PerspectiveAnalysisValidator.Validate(result, allowed);
                return result;
            }
            catch (InvalidOperationException) when (attempt < MaxAttempts)
            {
                // Reject the whole analysis, not just a troublesome viewpoint.
                // The next pass must cite the ORIGINAL retrieved evidence.
            }
            catch (InvalidOperationException)
            {
                // Explicitly report an unverifiable comparison to the educator
                // instead of failing the entire lesson or silently supplying
                // a generic pair of opposing positions.
            }
        }

        var inconclusive = new PerspectiveAnalysisOutput
        {
            Status = PerspectiveAnalysisValidator.Insufficient,
            Perspectives = [],
            AreasOfAgreement = [],
            EvidenceLimitations =
                "The automated Perspective Analyst twice returned a comparison " +
                "that failed required completeness or source-citation checks. " +
                "No competing interpretation was accepted as verified. " +
                "The retrieved research remains available for educator review; " +
                "this result does not establish whether scholarly disagreement exists.",
            DiscussionQuestions = []
        };
        PerspectiveAnalysisValidator.Validate(inconclusive, allowed);
        return inconclusive;
    }
}
