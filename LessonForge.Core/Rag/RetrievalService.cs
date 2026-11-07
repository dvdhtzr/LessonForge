// Copyright (c) 2026 — LessonForge. Original work.

using Microsoft.Extensions.AI;
using System.Security.Cryptography;
using System.Text;

namespace LessonForge.Core.Rag;

/// <summary>Bounds that keep a retrieval run fast and polite.</summary>
public sealed class RetrievalOptions
{
    /// <summary>Whether Wikipedia is enabled in the local source selection.</summary>
    public bool EnableWikipedia { get; set; } = true;

    /// <summary>How many top search hits to use as seed articles.</summary>
    public int MaxSeedArticles { get; set; } = 2;

    /// <summary>How many closely-linked articles (from the first seed) to also fetch.</summary>
    public int MaxLinkedArticles { get; set; } = 4;

    /// <summary>Legacy reference limit (external content fetches now require reviewed rights).</summary>
    public int MaxReferences { get; set; } = 8;

    /// <summary>Legacy reference concurrency (retained for configuration compatibility).</summary>
    public int FetchConcurrency { get; set; } = 4;
}

/// <summary>
/// The result of building a retrieval context for one topic: an in-memory vector index plus the
/// inventory of sources that went into it. Agents search it per sub-task; it is discarded when the
/// run ends.
/// </summary>
public sealed class RetrievalSession(InMemoryVectorIndex index, IReadOnlyList<SourceDocument> sources)
{
    public IReadOnlyList<SourceDocument> Sources { get; } = sources;
    public int ChunkCount => index.Count;

    /// <summary>The distinct sources, as citations.</summary>
    public IReadOnlyList<Citation> Citations { get; } = sources
        .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var source = g.First();
            return new Citation
            {
                Title = source.Title,
                Url = source.Url,
                ProviderId = source.ProviderId,
                LicenseId = source.Rights?.LicenseId ?? "",
                RightsPolicyUrl = source.Rights?.PolicyUrl ?? "",
                Attribution = source.Rights?.Attribution ?? "",
                DocumentSha256 = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(source.Text)))
            };
        }).ToList();

    public Task<IReadOnlyList<ScoredChunk>> SearchAsync(string query, int k = 6, CancellationToken ct = default)
        => index.SearchAsync(query, k, ct);
}

/// <summary>
/// Orchestrates rights-eligible Wikipedia article extracts and optional registered
/// providers through the existing chunk → embed → in-memory index. Third-party
/// external reference URLs are discovered but not ingested without permission.
/// </summary>
public sealed class RetrievalService(
    WikipediaClient wikipedia,
    SourceFetcher fetcher,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    RetrievalOptions? options = null,
    Chunker? chunker = null,
    SourceRegistry? registry = null)
{
    private readonly RetrievalOptions _options = options ?? new RetrievalOptions();
    private readonly Chunker _chunker = chunker ?? new Chunker();
    private readonly SourceRegistry _registry = registry ?? new SourceRegistry();

    /// <summary>
    /// Builds a <see cref="RetrievalSession"/> for a topic. <paramref name="progress"/>, if given,
    /// receives short human-readable status lines (surfaced to the UI as the research stage runs).
    /// </summary>
    /// <summary>
    /// Primary lesson research must have at least one rights-eligible source.
    /// Never return an empty index to the Research Librarian.
    /// </summary>
    public Task<RetrievalSession> BuildAsync(
        string topic, IProgress<string>? progress = null, CancellationToken ct = default)
        => BuildCoreAsync(topic, requireEvidence: true, progress, ct);

    /// <summary>
    /// Best-effort *additional* research for the Perspective Analyst. An empty,
    /// fully evaluated result is not a failure of the primary research: the
    /// original rights-eligible session remains available. Actual transport,
    /// cancellation and indexing faults are NOT hidden by this API.
    /// </summary>
    public async Task<RetrievalSession?> BuildSupplementaryAsync(
        string topic, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var session = await BuildCoreAsync(topic, requireEvidence: false, progress, ct);
        return session.Sources.Count == 0 ? null : session;
    }

    private async Task<RetrievalSession> BuildCoreAsync(
        string topic, bool requireEvidence, IProgress<string>? progress,
        CancellationToken ct)
    {
        var namedSubject = TopicIdentity.IsExplicitNamedSubject(topic);
        SourceDocument? exactArticle = null;
        IReadOnlyList<string> seedTitles;

        // A possessive description is not necessarily a canonical article
        // title: "Pablo Escobar's hippos" can legitimately resolve to an
        // article titled "Hippopotamuses in Colombia". This path requires
        // BOTH a focused article title and the owner's full name in the
        // content. Exact proper-name requests retain their strict lock.
        var descriptivePossessive = TopicIdentity.TryGetPossessiveTopic(
            topic, out _, out _);
        if (_options.EnableWikipedia && descriptivePossessive)
        {
            // This is an identity-bound description, not broad fuzzy discovery.
            // Do not expand related biography links or fetch the article again.
            namedSubject = true;
            progress?.Report($"Resolving the subject and owner in \"{topic}\"…");
            exactArticle = await wikipedia.FindPossessiveTopicAsync(
                topic, Math.Clamp(_options.MaxSeedArticles + 4, 1, 10), ct);
            if (exactArticle is not null)
            {
                progress?.Report($"Matched verified subject article: {exactArticle.Title}");
                seedTitles = [exactArticle.Title];
            }
            else
            {
                progress?.Report($"No article verifies both subject and owner for \"{topic}\"; checking reviewed providers.");
                seedTitles = [];
            }
        }
        else
        {
            // First try the requested Wikipedia title, including lowercase input.
            // We only lock to the direct result when its canonical title actually
            // matches the requested subject, never on fuzzy surname similarity.
            if (_options.EnableWikipedia && TopicIdentity.IsShortTitleCandidate(topic))
            {
                var direct = await wikipedia.GetArticleAsync(topic, ct);
                if (direct is not null && TopicIdentity.SameTitle(topic, direct.Title))
                {
                    exactArticle = direct;
                    namedSubject = true;
                }
            }

            if (_options.EnableWikipedia && namedSubject)
            {
                // Never allow a fuzzy title/surname hit to become the subject
                // of a named-person or other explicitly named-entity lesson.
                progress?.Report($"Looking up the exact Wikipedia subject \"{topic}\"…");

                if (exactArticle is null)
                {
                    var titles = await wikipedia.SearchTitlesAsync(topic, _options.MaxSeedArticles + 4, ct);
                    foreach (var title in titles.Where(title => TopicIdentity.SameTitle(topic, title)))
                    {
                        var candidate = await wikipedia.GetArticleAsync(title, ct);
                        if (candidate is not null && TopicIdentity.SameTitle(topic, candidate.Title))
                        {
                            exactArticle = candidate;
                            break;
                        }
                    }
                }

                if (exactArticle is null)
                {
                    progress?.Report($"No exact Wikipedia article for \"{topic}\"; checking reviewed providers for a full subject match.");
                    seedTitles = [];
                }
                else
                {
                    progress?.Report($"Matched exact Wikipedia subject: {exactArticle.Title}");
                    seedTitles = [exactArticle.Title];
                }
            }
            else if (_options.EnableWikipedia)
            {
                progress?.Report($"Searching Wikipedia for \"{topic}\"…");
                seedTitles = await wikipedia.SearchTitlesAsync(topic, _options.MaxSeedArticles, ct);
            }
            else
            {
                progress?.Report("Wikipedia disabled by local source settings; checking other enabled providers.");
                seedTitles = [];
            }
        }

        var documents = new List<SourceDocument>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreviewedReferenceCount = 0;

        // Seed Wikipedia article text and closely linked article text.
        for (var i = 0; i < seedTitles.Count; i++)
        {
            var title = seedTitles[i];
            var article = namedSubject ? exactArticle : await wikipedia.GetArticleAsync(title, ct);
            if (article is not null && seenUrls.Add(article.Url))
            {
                documents.Add(article);
                progress?.Report($"Fetched article: {article.Title}");
            }

            // Only expand linked articles from the primary (first) seed, to keep runs bounded.
            if (!namedSubject && i == 0 && _options.MaxLinkedArticles > 0)
            {
                var linked = await wikipedia.GetLinkedTitlesAsync(title, _options.MaxLinkedArticles * 2, ct);
                foreach (var linkedTitle in linked.Take(_options.MaxLinkedArticles))
                {
                    var linkedDoc = await wikipedia.GetArticleAsync(linkedTitle, ct);
                    if (linkedDoc is not null && seenUrls.Add(linkedDoc.Url))
                    {
                        documents.Add(linkedDoc);
                        progress?.Report($"Fetched linked article: {linkedDoc.Title}");
                    }
                }
            }

            // Wikipedia's external links are discovery metadata, NOT automatic
            // consent to retrieve or ingest another publisher's full content.
            if (!namedSubject)
            {
                var external = await wikipedia.GetExternalLinksAsync(title, 40, ct);
                unreviewedReferenceCount += external.Count;
            }
        }

        if (unreviewedReferenceCount > 0)
            progress?.Report($"Skipped {unreviewedReferenceCount} external reference link(s): " +
                "publisher-level rights have not been established.");

        // Only explicit, reviewed adapters can add further material. Candidate
        // providers failing or returning unusable records cannot block Wikipedia.
        var optional = await _registry.RetrieveEligibleAsync(topic, progress, ct);
        foreach (var source in optional)
        {
            // A full two-word named topic is a strong identity signal. Do not
            // ingest catalog records matching only a surname or first name,
            // even when their provider's relevance score was high.
            var needsExactSubject = descriptivePossessive ||
                (namedSubject &&
                 (exactArticle is null ||
                  topic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length == 2));
            if (needsExactSubject &&
                !(descriptivePossessive
                    ? TopicIdentity.TryGetPossessiveTopic(topic, out var owner, out var focus) &&
                      TopicIdentity.FocusMatchesTitle(focus, source.Title) &&
                      TopicIdentity.MentionsSubject(source.Text, owner)
                    : TopicIdentity.MentionsSubject(source.Title, topic) ||
                      TopicIdentity.MentionsSubject(source.Text, topic)))
            {
                progress?.Report($"Skipped {source.ProviderId} metadata: no full named-subject match.");
                continue;
            }
            if (seenUrls.Add(source.Url))
                documents.Add(source);
        }

        // This is the final model-input boundary, regardless of which adapter
        // produced a document. No missing/unknown rights flow into embeddings.
        var admitted = new List<SourceDocument>();
        foreach (var document in documents)
        {
            var decision = SourcePermissionGate.Evaluate(document);
            if (decision.Allowed)
                admitted.Add(document);
            else
                progress?.Report($"Skipped {document.ProviderId} evidence: {decision.Reason}");
        }
        if (admitted.Count == 0)
        {
            if (requireEvidence)
                throw new InvalidOperationException(
                    namedSubject
                        ? "No rights-eligible evidence identifies the requested named subject. " +
                          "LessonForge refused to substitute a similarly named person or topic."
                        : "No rights-eligible source evidence could be retrieved. " +
                          "The lesson cannot be generated from unsupported material.");

            // No model input or embedding occurs when an optional discovery query
            // yields zero eligible documents. The caller reuses primary research
            // and must explicitly report its limited perspective coverage.
            progress?.Report(
                "No additional rights-eligible perspective sources found; retaining primary research evidence.");
            return new RetrievalSession(new InMemoryVectorIndex(embeddings), []);
        }

        // Chunk + embed everything into a fresh index.
        progress?.Report("Embedding sources into the vector index…");
        var chunks = _chunker.ChunkAll(admitted);
        var index = new InMemoryVectorIndex(embeddings);
        await index.IngestAsync(chunks, ct);
        progress?.Report($"Indexed {chunks.Count} chunk(s) from {admitted.Count} rights-eligible source(s).");

        return new RetrievalSession(index, admitted);
    }
}
