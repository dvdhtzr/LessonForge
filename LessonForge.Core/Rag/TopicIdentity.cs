// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;

namespace LessonForge.Core.Rag;

/// <summary>
/// Identifies an explicit, proper-name-like subject and compares Wikipedia
/// canonical article titles without fuzzy surname similarity or token overlap.
/// This does not attempt to classify people: history/event titles also qualify.
/// A broad natural-language query still uses the normal topical search path.
/// </summary>
public static class TopicIdentity
{
    public static bool IsExplicitNamedSubject(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return false;

        var words = topic.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 6)
            return false;

        // A short Title Case name or proper noun is intended as a specific
        // subject; no fuzzy "closest surname" substitution is acceptable.
        return words.All(word => word.Length > 0 &&
            char.IsUpper(word[0]) &&
            word.All(character => char.IsLetter(character) || character is '-' or '\'' or '’'));
    }

    /// <summary>
    /// Probe short, unpunctuated titles even if the user types in lowercase.
    /// Only an exact canonical match enables the named-subject lock for these.
    /// </summary>
    public static bool IsShortTitleCandidate(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return false;
        var words = topic.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 2 and <= 6 &&
            words.All(word => word.Length > 0 &&
                word.All(character => char.IsLetter(character) || character is '-' or '\'' or '’'));
    }

    public static bool SameTitle(string topic, string actualTitle)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(actualTitle))
            return false;
        return string.Equals(NormalizeTitle(topic), NormalizeTitle(actualTitle),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Full-phrase, word-boundary check; never treat "Carroza" or "Carmen"
    /// alone as evidence of "Carmen Carrozza".
    /// </summary>
    public static bool MentionsSubject(string? text, string? topic)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(topic))
            return false;

        var content = text.Normalize(NormalizationForm.FormKC).Replace('_', ' ');
        var phrase = string.Join(' ', topic.Normalize(NormalizationForm.FormKC)
            .Replace('_', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (phrase.Length == 0) return false;

        for (var start = 0; start < content.Length;)
        {
            var at = content.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            var before = at == 0 || !char.IsLetterOrDigit(content[at - 1]);
            var end = at + phrase.Length;
            var after = end == content.Length || !char.IsLetterOrDigit(content[end]);
            if (before && after) return true;
            start = at + 1;
        }
        return false;
    }

    private static string NormalizeTitle(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Replace('_', ' ').Trim();
        var words = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // "The American Revolution" can legitimately resolve to
        // "American Revolution", but never to a different named person.
        if (words.Length > 2 && words[0].Equals("The", StringComparison.OrdinalIgnoreCase))
            words = words.Skip(1).ToArray();
        return string.Join(' ', words);
    }
}
