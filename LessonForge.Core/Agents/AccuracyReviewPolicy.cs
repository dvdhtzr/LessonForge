// Copyright (c) 2026 — LessonForge. Original work.

using System.Security.Cryptography;
using System.Text;
using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>
/// Deterministic boundary between model-authored review findings and educator-visible
/// artifacts. It validates controlled vocabulary and real item locations, assigns
/// repeatable IDs, and only consolidates findings with the same category, location
/// and explicitly named focal claim. No semantic guesswork or fabricated confidence.
/// </summary>
public static class AccuracyReviewPolicy
{
    public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.Ordinal)
    {
        "factual_accuracy",
        "missing_citation",
        "unsupported_claim",
        "misleading_wording",
        "missing_prerequisite",
        "instructional_gap",
        "assessment_alignment",
        "age_appropriateness",
        "perspective_fairness",
        "insufficient_evidence"
    };

    /// <summary>
    /// An Accuracy Editor scoped to exactly one host-selected section or question
    /// has an unambiguous location even when a local model omits that JSON field.
    /// Restore only missing/blank locations from that authoritative target.
    /// Never override GENERAL, an explicit (even invalid) model location, or
    /// deduce a location from a claim's wording. Multi-item scopes have no
    /// safe default and are not eligible for this recovery.
    /// </summary>
    public static int RestoreMissingLocationsForSingleTarget(
        FactCheckReviewOutput review, string target)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (target[0] is not ('S' or 'Q') ||
            !ValidLocation(target, null, null))
            throw new ArgumentException("A single section or question ID is required.", nameof(target));

        var restored = 0;
        foreach (var finding in review.Flags ?? [])
        {
            if (finding is not null && string.IsNullOrWhiteSpace(finding.Location))
            {
                finding.Location = target;
                restored++;
            }
        }
        return restored;
    }

    public static IReadOnlyList<string> InvalidFields(
        FactCheckReviewOutput review, int? sectionCount = null, int? questionCount = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        var invalid = new List<string>();

        if (review.Flags is null)
            invalid.Add("flags");
        else
            for (var i = 0; i < review.Flags.Count; i++)
            {
                var f = review.Flags[i];
                var prefix = $"flags[{i}]";
                if (f is null)
                {
                    invalid.Add(prefix);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(f.Claim)) invalid.Add($"{prefix}.claim");
                if (string.IsNullOrWhiteSpace(f.Issue)) invalid.Add($"{prefix}.issue");
                if (f.Severity is not ("High" or "Medium" or "Low" or "Informational"))
                    invalid.Add($"{prefix}.severity");
                if (!Categories.Contains(f.Category ?? "")) invalid.Add($"{prefix}.category");
                if (!ValidLocation(f.Location, sectionCount, questionCount))
                    invalid.Add($"{prefix}.location");
                if (string.IsNullOrWhiteSpace(f.Recommendation))
                    invalid.Add($"{prefix}.recommendation");
            }

        if (!double.IsFinite(review.CitationCoverage) || review.CitationCoverage is < 0 or > 1)
            invalid.Add("citation_coverage");
        if (string.IsNullOrWhiteSpace(review.Notes))
            invalid.Add("notes");

        return invalid;
    }

    /// <summary>
    /// Model observations are kept advisory. Merge only obvious duplicates;
    /// distinct categories, locations, or claims remain separate findings.
    /// </summary>
    public static List<VerificationFlag> Prepare(
        FactCheckReviewOutput review, LessonPlanOutput plan, QuizOutput quiz)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(quiz);
        var invalid = InvalidFields(review, plan.Sections?.Count, quiz.Questions?.Count);
        if (invalid.Count > 0)
            throw new InvalidOperationException(
                "Accuracy Editor returned invalid findings: " + string.Join(", ", invalid) + ".");

        var consolidated = new Dictionary<string, VerificationFlag>(StringComparer.Ordinal);
        foreach (var finding in review.Flags)
        {
            var key = $"{finding.Category}|{finding.Location}|{Normalize(finding.Claim)}";
            if (!consolidated.TryGetValue(key, out var existing))
            {
                var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
                consolidated.Add(key, new VerificationFlag
                {
                    Id = "LF-" + Convert.ToHexString(idBytes)[..10],
                    Claim = finding.Claim.Trim(),
                    Issue = finding.Issue.Trim(),
                    Severity = finding.Severity,
                    Category = finding.Category,
                    Location = finding.Location,
                    Recommendation = finding.Recommendation.Trim(),
                    Evidence = finding.Evidence?.Trim() ?? ""
                });
                continue;
            }

            // One root issue, one educator action. The most serious classification wins.
            if (Priority(finding.Severity) > Priority(existing.Severity))
            {
                existing.Severity = finding.Severity;
                existing.Issue = finding.Issue.Trim();
                existing.Recommendation = finding.Recommendation.Trim();
            }
            if (string.IsNullOrWhiteSpace(existing.Evidence) &&
                !string.IsNullOrWhiteSpace(finding.Evidence))
                existing.Evidence = finding.Evidence.Trim();
        }

        return consolidated.Values.ToList();
    }

    private static int Priority(string severity) => severity switch
    {
        "High" => 4,
        "Medium" => 3,
        "Low" => 2,
        _ => 1
    };

    private static string Normalize(string claim)
        => string.Join(" ", claim.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd('.', '!', '?', ':', ';');

    private static bool ValidLocation(string? location, int? sections, int? questions)
    {
        if (location == "GENERAL") return true;
        if (string.IsNullOrEmpty(location) || location.Length < 2) return false;
        var limit = location[0] switch
        {
            'S' => sections,
            'Q' => questions,
            _ => -1
        };
        if (!int.TryParse(location.AsSpan(1), out var index) ||
            index <= 0 || location[1] == '0') return false;
        return !limit.HasValue || index <= limit.Value;
    }
}
