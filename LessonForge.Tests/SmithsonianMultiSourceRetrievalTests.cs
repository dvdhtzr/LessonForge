using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public sealed class SmithsonianMultiSourceRetrievalTests
{
    private const string Metadata = """
        {"id":"edanmdm-nmah_12345","url":"edanmdm:nmah_12345","type":"edanmdm","title":"American Revolution military equipment","unitCode":"NMAH","content":{"descriptiveNonRepeating":{"metadata_usage":{"access":"CC0"},"online_media":{"media":[{"usage":{"access":"Usage conditions apply"},"content":"https://restricted.example/asset"}]}},"freetext":{"topic":[{"content":"American Revolution"}],"notes":[{"content":"Catalog description of an item related to the eighteenth-century conflict."}]}}}
        """;

    private sealed class Handler(string kind, bool fail = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = kind == "wiki"
                ? request.RequestUri!.Query.Contains("list=search")
                    ? """{"query":{"search":[{"title":"American Revolution"}]}}"""
                    : request.RequestUri.Query.Contains("prop=extracts")
                        ? """
                          {"query":{"pages":{"1":{"title":"American Revolution",
                          "canonicalurl":"https://en.wikipedia.org/wiki/American_Revolution",
                          "extract":"The American Revolution involved colonial resistance to British rule, an independence movement and a war. The conflict led to the formation of the United States of America."}}}}
                          """
                        : """{"query":{"pages":{}}}"""
                : Metadata;
            return Task.FromResult(new HttpResponseMessage(
                fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8,
                    kind == "wiki" ? "application/json" : "text/plain")
            });
        }
    }

    private sealed class UnlicensedLinksMustNeverBeFetched : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Unreviewed linked URL accessed.");
    }

    private sealed class TempCache : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lessonforge-si-rag-tests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    [Fact]
    public async Task Cc0SmithsonianCatalogAndWikipediaShareOneRightsGatedIndex()
    {
        using var cache = new TempCache();
        var wiki = new Handler("wiki");
        var si = new Handler("si");
        var session = await new RetrievalService(
            new WikipediaClient(new HttpClient(wiki)),
            new SourceFetcher(new HttpClient(new UnlicensedLinksMustNeverBeFetched())),
            new FakeEmbeddingGenerator(),
            new RetrievalOptions { MaxSeedArticles = 1, MaxLinkedArticles = 0 },
            registry: new SourceRegistry([
                new SmithsonianOpenAccessMetadataProvider(new HttpClient(si), cache.Path)
                { MaxShards = 1 }
            ])).BuildAsync("American Revolution");

        Assert.Equal(2, session.Sources.Count);
        Assert.True(session.ChunkCount >= 2);
        Assert.Contains(session.Citations, c =>
            c.ProviderId == "smithsonian-openaccess-metadata" &&
            c.LicenseId == "CC0-1.0" &&
            c.DocumentSha256.Length == 64);
        Assert.Contains(session.Citations, c =>
            c.ProviderId == "wikipedia-text" && c.LicenseId == "CC-BY-SA-4.0");
        Assert.DoesNotContain(session.Sources, s => s.Text.Contains("restricted.example"));
        Assert.All(session.Sources, s =>
            Assert.True(SourcePermissionGate.Evaluate(s).Allowed));
        Assert.Equal(1, si.Calls);
        var matches = await session.SearchAsync("American Revolution", k: 10);
        Assert.Contains(matches, match => match.Chunk.ProviderId == "smithsonian-openaccess-metadata");
    }

    [Fact]
    public async Task MissingSmithsonianShardFallsBackToPreviouslyTrustedWikipediaEvidence()
    {
        using var cache = new TempCache();
        var session = await new RetrievalService(
            new WikipediaClient(new HttpClient(new Handler("wiki"))),
            new SourceFetcher(new HttpClient(new UnlicensedLinksMustNeverBeFetched())),
            new FakeEmbeddingGenerator(),
            new RetrievalOptions { MaxSeedArticles = 1, MaxLinkedArticles = 0 },
            registry: new SourceRegistry([
                new SmithsonianOpenAccessMetadataProvider(new HttpClient(new Handler("si", fail:true)), cache.Path)
                { MaxShards = 1 }
            ])).BuildAsync("American Revolution");

        Assert.Single(session.Sources);
        Assert.Equal("wikipedia-text", session.Sources[0].ProviderId);
        Assert.True(session.ChunkCount > 0);
    }
}
