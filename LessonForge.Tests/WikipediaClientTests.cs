using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public class WikipediaClientTests
{
    // Routes canned MediaWiki JSON based on the query the client issues.
    private sealed class RoutingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var q = request.RequestUri!.Query;
            string body = q switch
            {
                _ when q.Contains("list=search") => """
                    {"query":{"search":[{"title":"Photosynthesis"},{"title":"Chlorophyll"}]}}
                    """,
                _ when q.Contains("prop=extracts") => """
                    {"query":{"pages":{"1":{"title":"Photosynthesis","canonicalurl":"https://en.wikipedia.org/wiki/Photosynthesis","extract":"Photosynthesis is the process by which plants convert light energy into chemical energy."}}}}
                    """,
                _ when q.Contains("prop=links") => """
                    {"query":{"pages":{"1":{"links":[{"title":"Chlorophyll"},{"title":"Calvin cycle"}]}}}}
                    """,
                _ when q.Contains("prop=extlinks") => """
                    {"query":{"pages":{"1":{"extlinks":[{"url":"https://example.org/a"},{"*":"https://example.org/b"}]}}}}
                    """,
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static WikipediaClient Client() => new(new HttpClient(new RoutingHandler()));

    [Fact]
    public async Task SearchTitles_parses_search_results()
    {
        var titles = await Client().SearchTitlesAsync("photosynthesis");
        Assert.Equal(["Photosynthesis", "Chlorophyll"], titles);
    }

    [Fact]
    public async Task GetArticle_parses_extract_and_url()
    {
        var doc = await Client().GetArticleAsync("Photosynthesis");
        Assert.NotNull(doc);
        Assert.Equal("Photosynthesis", doc!.Title);
        Assert.Equal("https://en.wikipedia.org/wiki/Photosynthesis", doc.Url);
        Assert.Contains("light energy", doc.Text);
        Assert.Equal(SourceKind.WikipediaArticle, doc.Kind);
    }

    [Fact]
    public async Task GetLinkedTitles_parses_links()
    {
        var links = await Client().GetLinkedTitlesAsync("Photosynthesis");
        Assert.Contains("Chlorophyll", links);
        Assert.Contains("Calvin cycle", links);
    }

    [Fact]
    public async Task GetExternalLinks_handles_both_url_and_star_shapes()
    {
        var urls = await Client().GetExternalLinksAsync("Photosynthesis");
        Assert.Contains("https://example.org/a", urls);
        Assert.Contains("https://example.org/b", urls);
    }
}
