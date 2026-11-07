// Copyright (c) 2026 — LessonForge. Original work.

using System.ComponentModel;
using System.Text.Json.Serialization;
using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>
/// Model-only contract for a review of exactly one host-selected S#/Q# item.
/// There is deliberately NO location property: item identity is supplied by
/// the workflow, not guessed by the local LLM.
/// </summary>
[Description("Review findings for one already-selected lesson section or quiz question.")]
public sealed class SingleItemFactCheckOutput
{
    [JsonPropertyName("flags")]
    [Description("Findings about the one target item only; [] is valid when no issue was found.")]
    public List<SingleItemFactCheckFinding> Flags { get; set; } = new();

    [JsonPropertyName("citation_coverage")]
    [Description("AI-estimated fraction 0..1 supported by source evidence.")]
    public double CitationCoverage { get; set; }

    [JsonPropertyName("notes")]
    [Description("A short nonempty note about the review and any limitations.")]
    public string Notes { get; set; } = "";
}

[Description("One substantive finding without a location or host-generated identifier.")]
public sealed class SingleItemFactCheckFinding
{
    [JsonPropertyName("claim")]
    public string Claim { get; set; } = "";

    [JsonPropertyName("issue")]
    public string Issue { get; set; } = "";

    [JsonPropertyName("severity")]
    [Description("REQUIRED: exactly High, Medium, Low or Informational.")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = "";

    [JsonPropertyName("evidence")]
    public string Evidence { get; set; } = "";
}

/// <summary>
/// Converts a single-item finding into the existing fully validated DTO. Every
/// finding receives the exact item ID chosen by the host. Missing severity or
/// any other required substance still fails; no default review is fabricated.
/// </summary>
public static class SingleItemFactCheckBoundary
{
    public static FactCheckReviewOutput AssignLocation(
        SingleItemFactCheckOutput model, string target)
    {
        ArgumentNullException.ThrowIfNull(model);
        // Reuse the existing strict target validator rather than parsing
        // arbitrary model-provided IDs. It will throw for S0, Q0, GENERAL,
        // comma-separated IDs, and malformed identifiers.
        AccuracyReviewPolicy.RestoreMissingLocationsForSingleTarget(
            new FactCheckReviewOutput(), target);

        var review = new FactCheckReviewOutput
        {
            Flags = model.Flags?.Select(f => f is null ? null! : new VerificationFlag
            {
                Claim = f.Claim,
                Issue = f.Issue,
                Severity = f.Severity,
                Category = f.Category,
                Recommendation = f.Recommendation,
                Evidence = f.Evidence,
                Location = target
            }).ToList()!,
            CitationCoverage = model.CitationCoverage,
            Notes = model.Notes
        };

        var invalid = AccuracyReviewPolicy.InvalidFields(review);
        if (invalid.Count > 0)
            throw new InvalidOperationException(
                "Single-item FactCheck returned invalid review fields: " +
                string.Join(", ", invalid) + ".");

        return review;
    }

    public static IReadOnlyList<string> InvalidFields(SingleItemFactCheckOutput model)
    {
        var review = new FactCheckReviewOutput
        {
            Flags = model.Flags?.Select(f => f is null ? null! : new VerificationFlag
            {
                Claim = f.Claim,
                Issue = f.Issue,
                Severity = f.Severity,
                Category = f.Category,
                Recommendation = f.Recommendation,
                Evidence = f.Evidence,
                Location = "S1"
            }).ToList()!,
            CitationCoverage = model.CitationCoverage,
            Notes = model.Notes
        };
        return AccuracyReviewPolicy.InvalidFields(review);
    }
}
