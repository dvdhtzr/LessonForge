// Copyright (c) 2026 — LessonForge. Original work.

using Microsoft.Extensions.AI;
using System.Security.Cryptography;
using System.Text;

namespace LessonForge.Core.Rag;

/// <summary>Bounds that keep a retrieval run fast and polite.</summary>
public sealed class RetrievalOptions
{
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
        progress?.Report($"Searching Wikipedia for \"{topic}\"…");
        var seedTitles = await wikipedia.SearchTitlesAsync(topic, _options.MaxSeedArticles, ct);

        var documents = new List<SourceDocument>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreviewedReferenceCount = 0;

        // Seed Wikipedia article text and closely linked article text.
        for (var i = 0; i < seedTitles.Count; i++)
        {
            var title = seedTitles[i];
            var article = await wikipedia.GetArticleAsync(title, ct);
            if (article is not null && seenUrls.Add(article.Url))
            {
                documents.Add(article);
                progress?.Report($"Fetched article: {article.Title}");
            }

            // Only expand linked articles from the primary (first) seed, to keep runs bounded.
            if (i == 0 && _options.MaxLinkedArticles > 0)
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
            var external = await wikipedia.GetExternalLinksAsync(title, 40, ct);
            unreviewedReferenceCount += external.Count;
        }

        if (unreviewedReferenceCount > 0)
            progress?.Report($"Skipped {unreviewedReferenceCount} external reference link(s): " +
                "publisher-level rights have not been established.");

        // Only explicit, reviewed adapters can add further material. Candidate
        // providers failing or returning unusable records cannot block Wikipedia.
        var optional = await _registry.RetrieveEligibleAsync(topic, progress, ct);
        foreach (var source in optional)
            if (seenUrls.Add(source.Url))
                documents.Add(source);

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
                    "No rights-eligible source evidence could be retrieved. " +
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
