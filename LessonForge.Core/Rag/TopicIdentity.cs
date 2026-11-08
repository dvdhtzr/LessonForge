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
    /// A topic such as "Pablo Escobar's hippos" names an owner and an
    /// associated subject, NOT necessarily an encyclopedia article with
    /// exactly that title. Treat such descriptions differently from an exact
    /// person-name request such as "Carmen Carrozza".
    /// </summary>
    public static bool TryGetPossessiveTopic(
        string? topic, out string owner, out string focus)
    {
        owner = "";
        focus = "";
        if (string.IsNullOrWhiteSpace(topic)) return false;

        var words = topic.Trim().Replace('’', '\'')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 7) return false;

        var possessiveAt = Array.FindIndex(words, w =>
            w.EndsWith("'s", StringComparison.OrdinalIgnoreCase));
        if (possessiveAt < 0 || possessiveAt > 3 ||
            possessiveAt == words.Length - 1)
            return false;

        var ownerWords = words.Take(possessiveAt + 1).ToArray();
        ownerWords[^1] = ownerWords[^1][..^2];
        if (ownerWords.Any(w => w.Length == 0 || !char.IsUpper(w[0]) ||
            !w.All(ch => char.IsLetter(ch) || ch is '-' or '\'')))
            return false;

        var focusWords = words.Skip(possessiveAt + 1).ToArray();
        if (focusWords.Any(w => w.Length == 0 ||
            !w.All(ch => char.IsLetter(ch) || ch is '-' or '\'')))
            return false;

        owner = string.Join(' ', ownerWords);
        focus = string.Join(' ', focusWords);
        return true;
    }

    /// <summary>
    /// Resolve a descriptive possessive topic only to a page whose TITLE
    /// describes the requested subject and whose TEXT names its owner.
    /// Matching the owner alone (e.g. a biography) is not sufficient.
    /// No title-specific, person-specific or animal-specific exceptions.
    /// </summary>
    public static bool MatchesPossessiveArticle(string topic, SourceDocument? article)
    {
        if (!TryGetPossessiveTopic(topic, out var owner, out var focus) ||
            article is null || article.ProviderId != "wikipedia-text" ||
            article.Kind != SourceKind.WikipediaArticle ||
            !SourcePermissionGate.Evaluate(article).Allowed)
            return false;

        return (SameTitle(topic, article.Title) ||
                FocusMatchesTitle(focus, article.Title)) &&
               (MentionsSubject(article.Text, owner) ||
                SameTitle(topic, article.Title));
    }

    /// <summary>
    /// Word-level title match with conservative inflection tolerance:
    /// "hippos" shares a substantive "hippo" prefix with
    /// "hippopotamuses", but "car" never matches "carpet".
    /// </summary>
    public static bool FocusMatchesTitle(string focus, string title)
    {
        var requestWords = SplitWords(focus)
            .Where(w => w.Length >= 3 && w is not ("the" or "and" or "for" or "with"))
            .ToArray();
        var titleWords = SplitWords(title).ToArray();
        if (requestWords.Length == 0) return false;

        return requestWords.All(request =>
        {
            var stem = request.EndsWith('s') && request.Length > 3
                ? request[..^1] : request;
            return titleWords.Any(word =>
                word == request || word == stem ||
                (stem.Length >= 4 && word.StartsWith(stem, StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// A summary may write "Pablo Escobar's hippos" as "Pablo Escobar's
    /// hippopotamuses". Require the complete named owner and the subject
    /// concept; never accept a similar surname or just the owner.
    /// </summary>
    public static bool SummaryIdentifiesSubject(string summary, string topic)
    {
        if (!TryGetPossessiveTopic(topic, out var owner, out var focus))
            return MentionsSubject(summary, topic);

        return MentionsSubject(summary, owner) &&
               FocusMatchesTitle(focus, summary);
    }

    private static IEnumerable<string> SplitWords(string input)
        => System.Text.RegularExpressions.Regex.Split(
            input.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),
            @"[^\p{L}\p{Nd}]+")
            .Where(word => word.Length > 0);

    /// <summary>
    /// Full-phrase, word-boundary check; never treat "Carroza" or "Carmen"
    /// alone as evidence of "Carmen Carrozza".
    /// </summary>
    public static bool MentionsSubject(string? text, string? topic)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(topic))
            return false;

        var content = text.Normalize(NormalizationForm.FormKC).Replace('_', ' ').Replace('’', '\'');
        var phrase = string.Join(' ', topic.Normalize(NormalizationForm.FormKC)
            .Replace('_', ' ').Replace('’', '\'')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
