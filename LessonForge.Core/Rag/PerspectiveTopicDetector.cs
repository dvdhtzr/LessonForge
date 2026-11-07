// Copyright (c) 2026 — LessonForge. Original work.

namespace LessonForge.Core.Rag;

/// <summary>
/// A conservative, explainable *suggestion* based exclusively on the first
/// rights-eligible article's text. It does not decide what is true, claim that
/// two sides are equally credible, or generate any content.
/// </summary>
public sealed record PerspectiveTopicSignal(
    bool Suggested, string Status, string Reason,
    string SourceTitle, string SourceUrl)
{
    public static PerspectiveTopicSignal Unavailable(string reason) =>
        new(false, "unavailable", reason, "", "");
}

public static class PerspectiveTopicDetector
{
    // Concrete signals that an article describes substantive scholarly,
    // methodological, historical or ethical disagreement. Do not use generic
    // words such as theory, research, experiment, study or uncertainty.
    private static readonly string[] s_strong =
    [
        "controversy", "controversial", "disputed", "discredited",
        "ethical concerns", "ethical controversy", "ethical issues",
        "research ethics", "replication crisis", "failed to replicate",
        "failure to replicate", "not replicated", "scientific misconduct",
        "fabricated data", "retracted", "widely criticized",
        "scholars disagree", "historians disagree", "methodological flaws"
    ];

    private static readonly string[] s_supporting =
    [
        "criticism", "criticized", "critics", "debate", "debated",
        "validity", "replication", "reproducibility", "methodological",
        "unethical", "allegations", "questions about", "challenged",
        "disagreement", "conflicting evidence", "interpretation"
    ];

    public static PerspectiveTopicSignal Evaluate(SourceDocument? article)
    {
        if (article is null || article.Kind != SourceKind.WikipediaArticle ||
            article.ProviderId != "wikipedia-text" ||
            !SourcePermissionGate.Evaluate(article).Allowed ||
            string.IsNullOrWhiteSpace(article.Text))
            return PerspectiveTopicSignal.Unavailable(
                "No eligible initial Wikipedia article was available for an evidence-based preview.");

        // Limit processing; Wikipedia extracts may be very large.
        var excerpt = article.Text.Length <= 60_000
            ? article.Text
            : article.Text[..60_000];

        static int CountSignals(string text, IEnumerable<string> phrases)
            => phrases.Count(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));

        var strong = CountSignals(excerpt, s_strong);
        var supporting = CountSignals(excerpt, s_supporting);
        // Deliberately require a cluster of indicators. A single historical
        // mention of criticism or debate must not label settled natural
        // phenomena controversial, and the user still chooses whether to run
        // the separate analyst.
        var suggested = strong >= 2 || (strong >= 1 && supporting >= 2);
        return new PerspectiveTopicSignal(
            suggested,
            suggested ? "suggested" : "not_suggested",
            suggested
                ? "The initial article describes multiple indicators of methodological, ethical or interpretive dispute. You may ask the Perspective Analyst to examine the supporting evidence."
                : "The initial article did not provide enough concrete dispute indicators to recommend a separate perspective analysis.",
            article.Title,
            article.Url);
    }
}

/// <summary>
/// A quick, anonymous preview before a lesson run. Only the requested topic
/// and one relevant Wikipedia article are considered; it never runs Ollama,
/// embeds data or searches unreviewed publishers.
/// </summary>
public sealed class InitialArticlePerspectiveProbe(WikipediaClient wikipedia)
{
    public async Task<PerspectiveTopicSignal> CheckAsync(
        string topic, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Trim().Length is < 3 or > 120)
            return PerspectiveTopicSignal.Unavailable(
                "Enter a topic between 3 and 120 characters to inspect an initial article.");

        var requested = topic.Trim();
        if (TopicIdentity.TryGetPossessiveTopic(requested, out _, out _))
        {
            // The research and preflight paths use the SAME constrained
            // resolution rule: an article must identify both the full owner
            // and the subject concept in its title.
            var matching = await wikipedia.FindPossessiveTopicAsync(requested, limit: 6, ct: ct);
            return PerspectiveTopicDetector.Evaluate(matching);
        }

        var direct = await wikipedia.GetArticleAsync(requested, ct);
        if (direct is not null && TopicIdentity.SameTitle(requested, direct.Title))
            return PerspectiveTopicDetector.Evaluate(direct);

        var titles = await wikipedia.SearchTitlesAsync(requested, limit: 3, ct: ct);
        if (TopicIdentity.IsExplicitNamedSubject(requested))
        {
            // Avoid suggesting a debate about a similarly named but unrelated
            // person, experiment or historical event.
            var exact = titles.FirstOrDefault(t => TopicIdentity.SameTitle(requested, t));
            if (exact is null) return PerspectiveTopicSignal.Unavailable(
                "The requested named topic did not match an exact Wikipedia article.");
            var match = await wikipedia.GetArticleAsync(exact, ct);
            return PerspectiveTopicDetector.Evaluate(
                match is not null && TopicIdentity.SameTitle(requested, match.Title) ? match : null);
        }

        foreach (var title in titles.Take(2))
        {
            var match = await wikipedia.GetArticleAsync(title, ct);
            if (match is not null)
                return PerspectiveTopicDetector.Evaluate(match);
        }

        return PerspectiveTopicSignal.Unavailable(
            "No suitable initial article was available for a perspective preview.");
    }
}
