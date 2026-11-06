using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

/// <summary>
/// Deterministic integration test: two licensed sources travel into the SAME
/// existing embedding/index path, while an unlicensed cited web page does not.
/// </summary>
public sealed class MultiSourceRetrievalTests
{
    private sealed class WikipediaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            var json = query switch
            {
                _ when query.Contains("list=search") =>
                    """{"query":{"search":[{"title":"Earthquake"}]}}""",
                _ when query.Contains("prop=extracts") =>
                    """
                    {"query":{"pages":{"1":{"title":"Earthquake",
                      "canonicalurl":"https://en.wikipedia.org/wiki/Earthquake",
                      "extract":"An earthquake releases seismic energy and causes waves to travel through Earth's crust. Scientists measure seismic events with instruments and study the resulting effects."}}}}
                    """,
                _ when query.Contains("prop=extlinks") =>
                    """
                    {"query":{"pages":{"1":{"extlinks":[
                      {"url":"https://unreviewed.example/copyrighted-article"}]}}}}
                    """,
                _ => """{"query":{"pages":{}}}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ForbiddenReferenceHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Unlicensed URL must never be fetched.");
        }
    }

    private sealed class UsgsFixtureAdapter : IEducationalSourceProvider
    {
        public string Id => "usgs-earthquakes";
        public bool SupportsTopic(string topic) => topic.Contains("earthquake",
            StringComparison.OrdinalIgnoreCase);
        public Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
            string topic, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SourceDocument>>(
            [
                new SourceDocument
                {
                    Title = "USGS event example",
                    Url = "https://earthquake.usgs.gov/earthquakes/eventpage/us1234",
                    Text = "USGS earthquake event us1234: magnitude 6.1 near Example City on a specific date. " +
                        "This observation is limited to that seismic event.",
                    Kind = SourceKind.UsgsEarthquake,
                    ProviderId = "usgs-earthquakes",
                    CanonicalId = "us1234",
                    Rights = ReviewedSourceRights.UsgsEarthquakeEvents,
                    RetrievedAtUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)
                }
            ]);
    }

    [Fact]
    public async Task WikipediaAndUsgs_SharedIndexAndProvenance_NoUnlicensedFetch()
    {
        var forbidden = new ForbiddenReferenceHandler();
        var retrieval = new RetrievalService(
            new WikipediaClient(new HttpClient(new WikipediaHandler())),
            new SourceFetcher(new HttpClient(forbidden)),
            new FakeEmbeddingGenerator(),
            new RetrievalOptions { MaxSeedArticles = 1, MaxLinkedArticles = 0, MaxReferences = 8 },
            registry: new SourceRegistry([new UsgsFixtureAdapter()]));

        var session = await retrieval.BuildAsync("How do earthquakes work?");
        Assert.Equal(2, session.Sources.Count);
        Assert.True(session.ChunkCount >= 2);
        Assert.Equal(0, forbidden.Calls);
        Assert.Contains(session.Citations, c =>
            c.ProviderId == "wikipedia-text" && c.LicenseId == "CC-BY-SA-4.0");
        Assert.Contains(session.Citations, c =>
            c.ProviderId == "usgs-earthquakes" &&
            c.LicenseId == "US-PD" && c.DocumentSha256.Length == 64);
        var found = await session.SearchAsync("earthquake");
        Assert.NotEmpty(found);
        Assert.All(found, hit =>
        {
            Assert.NotEmpty(hit.Chunk.ProviderId);
            Assert.NotEmpty(hit.Chunk.LicenseId);
            Assert.Equal(64, hit.Chunk.DocumentSha256.Length);
        });
        Assert.DoesNotContain(session.Citations, c =>
            c.Url.Contains("unreviewed.example", StringComparison.OrdinalIgnoreCase));
    }
}
