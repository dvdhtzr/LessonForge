// Copyright (c) 2026 — LessonForge. Original work.

using Microsoft.Extensions.AI;

namespace LessonForge.Core.Rag;

/// <summary>Bounds that keep a retrieval run fast and polite.</summary>
public sealed class RetrievalOptions
{
    /// <summary>How many top search hits to use as seed articles.</summary>
    public int MaxSeedArticles { get; set; } = 2;

    /// <summary>How many closely-linked articles (from the first seed) to also fetch.</summary>
    public int MaxLinkedArticles { get; set; } = 4;

    /// <summary>How many external cited references to attempt to fetch.</summary>
    public int MaxReferences { get; set; } = 8;

    /// <summary>Max concurrent reference fetches.</summary>
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
        .GroupBy(s => s.Url)
        .Select(g => new Citation { Title = g.First().Title, Url = g.Key })
        .ToList();

    public Task<IReadOnlyList<ScoredChunk>> SearchAsync(string query, int k = 6, CancellationToken ct = default)
        => index.SearchAsync(query, k, ct);
}

/// <summary>
/// Orchestrates the RAG ingest: English Wikipedia (seed + closely-linked articles) plus freely
/// fetchable cited references → chunk → embed → in-memory index. Built once per generation run.
/// </summary>
public sealed class RetrievalService(
    WikipediaClient wikipedia,
    SourceFetcher fetcher,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    RetrievalOptions? options = null,
    Chunker? chunker = null)
{
    private readonly RetrievalOptions _options = options ?? new RetrievalOptions();
    private readonly Chunker _chunker = chunker ?? new Chunker();

    /// <summary>
    /// Builds a <see cref="RetrievalSession"/> for a topic. <paramref name="progress"/>, if given,
    /// receives short human-readable status lines (surfaced to the UI as the research stage runs).
    /// </summary>
    public async Task<RetrievalSession> BuildAsync(
        string topic, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report($"Searching Wikipedia for \"{topic}\"…");
        var seedTitles = await wikipedia.SearchTitlesAsync(topic, _options.MaxSeedArticles, ct);

        var documents = new List<SourceDocument>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referenceUrls = new List<string>();

        // Seed articles + their linked articles and cited references.
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

            foreach (var refUrl in await wikipedia.GetExternalLinksAsync(title, 40, ct))
                referenceUrls.Add(refUrl);
        }

        // Fetch a bounded set of cited references concurrently.
        var toFetch = referenceUrls.Distinct(StringComparer.OrdinalIgnoreCase).Take(_options.MaxReferences).ToList();
        if (toFetch.Count > 0)
            progress?.Report($"Fetching {toFetch.Count} cited reference(s)…");

        using var gate = new SemaphoreSlim(_options.FetchConcurrency);
        var fetchTasks = toFetch.Select(async refUrl =>
        {
            await gate.WaitAsync(ct);
            try { return await fetcher.TryFetchAsync(refUrl, citedBy: seedTitles.FirstOrDefault(), ct); }
            finally { gate.Release(); }
        });

        foreach (var fetched in await Task.WhenAll(fetchTasks))
        {
            if (fetched is not null && seenUrls.Add(fetched.Url))
            {
                documents.Add(fetched);
                progress?.Report($"Fetched reference: {fetched.Title}");
            }
        }

        // Chunk + embed everything into a fresh index.
        progress?.Report("Embedding sources into the vector index…");
        var chunks = _chunker.ChunkAll(documents);
        var index = new InMemoryVectorIndex(embeddings);
        await index.IngestAsync(chunks, ct);
        progress?.Report($"Indexed {chunks.Count} chunk(s) from {documents.Count} source(s).");

        return new RetrievalSession(index, documents);
    }
}
