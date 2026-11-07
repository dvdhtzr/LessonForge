using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public sealed class WikibooksRetrievalIntegrationTests
{
    private sealed class WikibooksHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            var json = query.Contains("list=search", StringComparison.Ordinal)
                ? """{"query":{"search":[{"title":"Biology/Photosynthesis"}]}}"""
                : """
                  {"parse":{"title":"Biology/Photosynthesis",
                    "text":"<div class=\"mw-parser-output\"><p>Photosynthesis occurs in plants when sunlight powers chemical processes using water and carbon dioxide to make sugars.</p><p>Chlorophyll plays an important role in capturing sunlight for the chemical processes that ultimately support the life of plants.</p></div>"}}
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class WikipediaHandler(bool forbidden = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (forbidden)
                throw new InvalidOperationException("Disabled Wikipedia must never be called.");

            var query = request.RequestUri!.Query;
            var json = query.Contains("list=search", StringComparison.Ordinal)
                ? """{"query":{"search":[{"title":"Photosynthesis"}]}}"""
                : query.Contains("prop=extracts", StringComparison.Ordinal)
                    ? """
                      {"query":{"pages":{"1":{"title":"Photosynthesis",
                        "canonicalurl":"https://en.wikipedia.org/wiki/Photosynthesis",
                        "extract":"Photosynthesis uses sunlight to convert water and carbon dioxide into sugars in plants. The process can release oxygen and sustain ecosystems."}}}}
                      """
                    : """{"query":{"pages":{}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class NoExternalFetcher : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Unreviewed web references are forbidden.");
    }

    [Fact]
    public async Task WikibooksAndWikipediaShareCitedRightsCheckedIndex()
    {
        var session = await new RetrievalService(
            new WikipediaClient(new HttpClient(new WikipediaHandler())),
            new SourceFetcher(new HttpClient(new NoExternalFetcher())),
            new FakeEmbeddingGenerator(),
            new RetrievalOptions { MaxSeedArticles = 1, MaxLinkedArticles = 0 },
            registry: new SourceRegistry(
                [new WikibooksProvider(new HttpClient(new WikibooksHandler()))]))
            .BuildAsync("photosynthesis");

        Assert.Equal(2, session.Sources.Count);
        Assert.Contains(session.Citations, c =>
            c.ProviderId == "wikibooks-text" && c.LicenseId == "CC-BY-SA-4.0");
        Assert.Contains(session.Citations, c => c.ProviderId == "wikipedia-text");
        Assert.All(session.Sources, s =>
            Assert.True(SourcePermissionGate.Evaluate(s).Allowed));
        Assert.True(session.ChunkCount >= 2);

        var chunks = await session.SearchAsync("photosynthesis", k: 12);
        Assert.Contains(chunks, c => c.Chunk.ProviderId == "wikibooks-text");
        Assert.Contains(chunks, c => c.Chunk.ProviderId == "wikipedia-text");
        Assert.All(chunks, chunk => Assert.Equal(64, chunk.Chunk.DocumentSha256.Length));
    }

    [Fact]
    public async Task DisabledWikipedia_StillPermitsReviewedWikibooksEvidence()
    {
        var wiki = new WikipediaHandler(forbidden: true);
        var session = await new RetrievalService(
            new WikipediaClient(new HttpClient(wiki)),
            new SourceFetcher(new HttpClient(new NoExternalFetcher())),
            new FakeEmbeddingGenerator(),
            new RetrievalOptions { EnableWikipedia = false },
            registry: new SourceRegistry(
                [new WikibooksProvider(new HttpClient(new WikibooksHandler()))],
                wikipediaEnabled: false))
            .BuildAsync("photosynthesis");

        Assert.Equal(0, wiki.Calls);
        Assert.Single(session.Sources);
        Assert.Equal("wikibooks-text", session.Sources[0].ProviderId);
        Assert.True(session.ChunkCount > 0);
    }
}
