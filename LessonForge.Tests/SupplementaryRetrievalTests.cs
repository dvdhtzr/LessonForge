using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

/// <summary>
/// The Perspective Analyst's secondary query is an enrichment attempt, not a
/// prerequisite for processing primary, permission-checked lesson evidence.
/// </summary>
public sealed class SupplementaryRetrievalTests
{
    private sealed class RoutingWikipediaHandler : HttpMessageHandler
    {
        public int PrimarySearches { get; private set; }
        public int SupplementarySearches { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            string json;
            if (query.Contains("list=search", StringComparison.Ordinal))
            {
                if (query.Contains("interpretations", StringComparison.OrdinalIgnoreCase))
                {
                    SupplementarySearches++;
                    json = """{"query":{"search":[]}}""";
                }
                else
                {
                    PrimarySearches++;
                    json = """{"query":{"search":[{"title":"Earthquake"}]}}""";
                }
            }
            else if (query.Contains("prop=extracts", StringComparison.Ordinal))
                json = """
                    {"query":{"pages":{"1":{"title":"Earthquake",
                    "canonicalurl":"https://en.wikipedia.org/wiki/Earthquake",
                    "extract":"An earthquake is ground shaking associated with seismic waves. Earthquake waves travel through Earth's crust. Scientists use seismometers to record and study earthquakes."}}}}
                    """;
            else
                json = """{"query":{"pages":{}}}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static RetrievalService CreateService(RoutingWikipediaHandler handler)
        => new(
            new WikipediaClient(new HttpClient(handler)),
            new SourceFetcher(new HttpClient(new ThrowingReferenceHandler())),
            new FakeEmbeddingGenerator(),
            new RetrievalOptions
            {
                MaxSeedArticles = 1,
                MaxLinkedArticles = 0,
                MaxReferences = 0
            });

    private sealed class ThrowingReferenceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Unreviewed links must never be fetched.");
    }

    [Fact]
    public async Task SecondarySearchWithoutSources_DoesNotDiscardPrimaryEvidence()
    {
        var handler = new RoutingWikipediaHandler();
        var service = CreateService(handler);

        var primary = await service.BuildAsync("Earthquakes");
        var before = Assert.Single(primary.Sources);
        Assert.True(SourcePermissionGate.Evaluate(before).Allowed);
        Assert.True(primary.ChunkCount > 0);
        var originalCitations = primary.Citations.Select(c => c.Url).ToArray();

        var supplementary = await service.BuildSupplementaryAsync(
            "Earthquakes interpretations historical scholarly debates");

        Assert.Null(supplementary);
        Assert.Equal(originalCitations, primary.Citations.Select(c => c.Url));
        Assert.Equal("https://en.wikipedia.org/wiki/Earthquake", before.Url);
        Assert.Equal(1, handler.PrimarySearches);
        Assert.Equal(1, handler.SupplementarySearches);
        Assert.NotEmpty(await primary.SearchAsync("earthquake"));
    }

    [Fact]
    public async Task EmptyPrimarySearch_StillFailsClosed()
    {
        var handler = new RoutingWikipediaHandler();
        var service = CreateService(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.BuildAsync("Earthquakes interpretations historical scholarly debates"));

        Assert.Contains("No rights-eligible source evidence", error.Message);
        Assert.Equal(1, handler.SupplementarySearches);
    }

    [Fact]
    public async Task EmptySecondarySearch_DoesNotProducePhantomCitations()
    {
        var handler = new RoutingWikipediaHandler();
        var service = CreateService(handler);
        var secondary = await service.BuildSupplementaryAsync(
            "Earthquakes interpretations historical scholarly debates");

        Assert.Null(secondary);
        Assert.Equal(1, handler.SupplementarySearches);
    }

    [Fact]
    public async Task SecondarySearch_WithSources_StillProducesAnIndexedSession()
    {
        var handler = new RoutingWikipediaHandler();
        var service = CreateService(handler);

        var result = await service.BuildSupplementaryAsync("Earthquakes");
        Assert.NotNull(result);
        Assert.Single(result!.Citations);
        Assert.True(result.ChunkCount > 0);
        Assert.All(result.Sources, source =>
            Assert.True(SourcePermissionGate.Evaluate(source).Allowed));
    }

    [Fact]
    public async Task CancellationOfOptionalDiscovery_IsNotSwallowed()
    {
        var handler = new RoutingWikipediaHandler();
        var service = CreateService(handler);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.BuildSupplementaryAsync("Earthquakes", ct: cancel.Token);
        });
    }
}
