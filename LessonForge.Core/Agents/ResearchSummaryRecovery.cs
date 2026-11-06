// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>
/// A conservative completeness check for a short generated prose field, plus
/// ONE targeted regeneration using the original rights-eligible evidence.
/// This detects obvious sentence fragments, not the truth or grammatical
/// completeness of every possible sentence.
/// </summary>
public static class ResearchSummaryRecovery
{
    private static readonly HashSet<string> s_danglingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "and", "or", "but", "for", "to", "from",
        "by", "in", "on", "with", "without", "into", "at", "about",
        "between", "among", "through", "as", "that", "which", "who",
        "whose", "where", "when", "because", "although", "while"
    };

    /// <summary>
    /// Reject missing terminal punctuation, explicit ellipses and a visibly
    /// dangling last word even if a model placed a period after it.
    /// A sentence ending in punctuation can still be semantically incomplete;
    /// the educator and Accuracy Editor remain responsible for that.
    /// </summary>
    public static bool LooksComplete(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return false;
        var text = summary.Trim().TrimEnd('"', '\'', '”', '’', ')', ']');
        if (text.Length == 0 || text.EndsWith("...", StringComparison.Ordinal) ||
            text.EndsWith('…') || text[^1] is not ('.' or '?' or '!'))
            return false;

        var cursor = text.Length - 2; // skip final sentence punctuation
        while (cursor >= 0 && !char.IsLetter(text[cursor]))
            cursor--;

        if (cursor < 0) return false;
        var end = cursor;
        while (cursor >= 0 && char.IsLetter(text[cursor]))
            cursor--;

        return !s_danglingWords.Contains(text[(cursor + 1)..(end + 1)]);
    }

    /// <summary>
    /// Preserve all approved grounded facts, citations and source inventory.
    /// Regenerate ONLY topic_summary if it looks cut off. Do not paste an
    /// invented ending onto an incomplete sentence. If the attempt also
    /// fails, stop before passing research to an educator or planner.
    /// </summary>
    public static async Task<ResearchDraftOutput> EnsureCompleteAsync(
        ResearchDraftOutput draft,
        Func<CancellationToken, Task<string>> regenerateSummary,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(regenerateSummary);

        if (LooksComplete(draft.TopicSummary))
            return draft;

        ct.ThrowIfCancellationRequested();
        progress?.Report("Research summary appears incomplete; regenerating a short evidence-grounded summary…");

        var replacement = await regenerateSummary(ct);
        ct.ThrowIfCancellationRequested();

        if (!LooksComplete(replacement))
            throw new InvalidOperationException(
                "Research summary remained incomplete after one targeted regeneration. " +
                "The incomplete report was not sent for educator review. " +
                "Check the local model's output/context limits and try again.");

        draft.TopicSummary = replacement.Trim();
        progress?.Report("Research summary completeness check passed.");
        return draft;
    }
}
