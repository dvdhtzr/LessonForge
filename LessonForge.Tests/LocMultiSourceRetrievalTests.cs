using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

/// <summary>
/// End-to-end *retrieval* integration: a real LoC JSON adapter and the existing
/// Wikipedia client contribute to the same rights-gated vector index. The model
/// stage remains out of scope for deterministic, network-free tests.
/// </summary>
public sealed class LocMultiSourceRetrievalTests
{
    private sealed class WikiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var query = request.RequestUri!.Query;
            var json = query switch
            {
                _ when query.Contains("list=search", StringComparison.Ordinal) =>
                    """{"query":{"search":[{"title":"United States Constitution"}]}}""",
                _ when query.Contains("prop=extracts", StringComparison.Ordinal) =>
                    """
                    {"query":{"pages":{"1":{"title":"United States Constitution",
                    "canonicalurl":"https://en.wikipedia.org/wiki/United_States_Constitution",
                    "extract":"The Constitution establishes the framework of the United States federal government, describing its institutions and the distribution of governmental powers. It was adopted in the late eighteenth century."}}}}
                    """,
                _ => """{"query":{"pages":{}}}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class LoCHandler(bool unavailable = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var json = """
                {"results":[{"id":"http://www.loc.gov/item/11223344/",
                    "title":"A congressional debate about constitutional government",
                    "description":["Historical catalog record of the Congress debating constitutional authority."],
                    "date":"1840",
                    "partof":["A Century of Lawmaking for a New Nation: U.S. Congressional Documents and Debates, 1774-1875"]}]}
                """;
            return Task.FromResult(new HttpResponseMessage(
                unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static RetrievalService Build(LoCHandler handler)
        => new(
            new WikipediaClient(new HttpClient(new WikiHandler())),
            new SourceFetcher(new HttpClient(new NoExternalFetcher())),
            new FakeEmbeddingGenerator(),
            new RetrievalOptions { MaxSeedArticles = 1, MaxLinkedArticles = 0 },
            registry: new SourceRegistry([
                new LocLawmakingCatalogProvider(new HttpClient(handler))
            ]));

    private sealed class NoExternalFetcher : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("An unreviewed third-party URL was fetched.");
    }

    [Fact]
    public async Task ConstitutionalHistoryUsesOfficialLoCAndWikipediaInOneEvidenceSession()
    {
        var handler = new LoCHandler();
        var session = await Build(handler).BuildAsync("United States Constitution");

        Assert.Equal(2, session.Sources.Count);
        Assert.Equal(1, handler.Calls);
        Assert.True(session.ChunkCount >= 2);
        Assert.Contains(session.Citations, c =>
            c.ProviderId == "loc-century-of-lawmaking" &&
            c.LicenseId == "US-PD" &&
            c.Url == "https://www.loc.gov/item/11223344/" &&
            c.DocumentSha256.Length == 64);
        Assert.Contains(session.Citations, c =>
            c.ProviderId == "wikipedia-text" && c.LicenseId == "CC-BY-SA-4.0");
        Assert.All(session.Sources, doc =>
            Assert.True(SourcePermissionGate.Evaluate(doc).Allowed));

        var indexed = await session.SearchAsync("congressional government", k: 10);
        Assert.Contains(indexed, hit =>
            hit.Chunk.ProviderId == "loc-century-of-lawmaking" &&
            hit.Chunk.LicenseId == "US-PD" &&
            hit.Chunk.DocumentSha256.Length == 64);
    }

    [Fact]
    public async Task LoCProviderUnavailabilityDoesNotBlockWikipediaResearch()
    {
        var handler = new LoCHandler(unavailable: true);
        var session = await Build(handler).BuildAsync("United States Constitution");

        Assert.Single(session.Sources);
        Assert.Equal("wikipedia-text", session.Sources[0].ProviderId);
        Assert.Single(session.Citations);
        Assert.True(session.ChunkCount > 0);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnrelatedTopicDoesNotQueryLoC()
    {
        var handler = new LoCHandler();
        var session = await Build(handler).BuildAsync("Photosynthesis and chlorophyll");
        Assert.Single(session.Sources);
        Assert.Equal(0, handler.Calls);
    }
}
