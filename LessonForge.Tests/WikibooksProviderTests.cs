using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

/// <summary>
/// Uses synthetic MediaWiki Action API responses. No network, keys, model
/// tokens, image downloads or full-book crawling are involved.
/// </summary>
public sealed class WikibooksProviderTests
{
    private sealed class WikiHandler(bool noResults = false, bool redirect = false,
        bool oversized = false) : HttpMessageHandler
    {
        public readonly List<Uri> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            string body;
            if (query.Contains("list=search", StringComparison.Ordinal))
            {
                body = noResults
                    ? """{"query":{"search":[]}}"""
                    : """
                      {"query":{"search":[
                        {"title":"Biology/Photosynthesis"},
                        {"title":"Wikibooks:Copyrights"},
                        {"title":"Biology/Photosynthesis"}
                      ]}}
                      """;
            }
            else if (query.Contains("action=parse", StringComparison.Ordinal))
            {
                var title = redirect ? "Another Lesson" : "Biology/Photosynthesis";
                body = $$"""
                    {"parse":{"title":"{{title}}",
                       "text":"<div class=\"mw-parser-output\"><h2>Teaching photosynthesis</h2><p>Photosynthesis is the way plants convert sunlight and water into energy-rich sugars, releasing oxygen as a byproduct of the process.</p><p>Chlorophyll in green plant cells absorbs solar light, helping a sequence of chemical reactions produce glucose that supports plant growth.</p><table><tr><td>UNREVIEWED TABLE CONTENT</td></tr></table><figure>UNREVIEWED IMAGE</figure><ul><li>Students can investigate the role of sunlight by observing how leaves respond to light over time.</li></ul><script>INJECTION</script></div>"
                    }}
                    """;
            }
            else body = "{}";

            if (oversized && query.Contains("action=parse"))
                body += new string('X', 605_000);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task OfficialApiTextOnly_ProducesAttributableAndRightsGatedChapter()
    {
        var handler = new WikiHandler();
        var provider = new WikibooksProvider(new HttpClient(handler));

        var result = await provider.RetrieveAsync("photosynthesis");

        var doc = Assert.Single(result);
        Assert.Equal("wikibooks-text", doc.ProviderId);
        Assert.Equal(SourceKind.WikibooksPage, doc.Kind);
        Assert.Equal("Biology/Photosynthesis", doc.CanonicalId);
        Assert.Equal("https://en.wikibooks.org/wiki/Biology%2FPhotosynthesis", doc.Url);
        Assert.Contains("plants convert sunlight", doc.Text);
        Assert.Contains("leaves respond", doc.Text);
        Assert.DoesNotContain("UNREVIEWED", doc.Text);
        Assert.DoesNotContain("INJECTION", doc.Text);
        Assert.DoesNotContain("<", doc.Text);
        Assert.True(SourcePermissionGate.Evaluate(doc).Allowed);
        Assert.Equal("CC-BY-SA-4.0", doc.Rights?.LicenseId);
        Assert.False(doc.Rights?.MayRedistribute);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, uri =>
        {
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("en.wikibooks.org", uri.Host);
            Assert.Equal("/w/api.php", uri.AbsolutePath);
        });
    }

    [Fact]
    public async Task MissingSearchResults_DoesNotInventChapters()
    {
        var handler = new WikiHandler(noResults: true);
        var found = await new WikibooksProvider(new HttpClient(handler))
            .RetrieveAsync("a nonexistent subject");
        Assert.Empty(found);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RedirectedOrOversizedContent_IsSkipped()
    {
        var redirected = await new WikibooksProvider(
            new HttpClient(new WikiHandler(redirect: true)))
            .RetrieveAsync("photosynthesis");
        Assert.Empty(redirected);

        var oversized = await new WikibooksProvider(
            new HttpClient(new WikiHandler(oversized: true)))
            .RetrieveAsync("photosynthesis");
        Assert.Empty(oversized);
    }

    [Fact]
    public void UnknownMediaAndUnreviewedHtml_AreNotEmbedded()
    {
        const string html = """
            <div class="mw-parser-output">
              <nav><p>Navigation text must never be embedded, even if long.</p></nav>
              <p>A real educational explanation about biological adaptation and inherited traits, offered here in regular paragraph text.</p>
              <img src="https://unreviewed.example/image.jpg">
              <p>Another description connecting observed biological evidence to the natural processes that shape a population.</p>
              <table><tr><td>Unreviewed table text</td></tr></table>
              <script>Never execute or embed scripts.</script>
            </div>
            """;
        var extracted = WikibooksProvider.ExtractInstructionalText(html);
        Assert.Contains("real educational explanation", extracted);
        Assert.Contains("Another description", extracted);
        Assert.DoesNotContain("Navigation text", extracted);
        Assert.DoesNotContain("Unreviewed table", extracted);
        Assert.DoesNotContain("scripts", extracted);
        Assert.DoesNotContain("https://unreviewed", extracted);
    }

    [Theory]
    [InlineData("https://en.wikibooks.org/wiki/Biology%2FPhotosynthesis", true)]
    [InlineData("https://en.wikibooks.org/wiki/Another_Chapter", false)]
    [InlineData("https://en.wikipedia.org/wiki/Biology%2FPhotosynthesis", false)]
    [InlineData("https://en.wikibooks.org/wiki/Biology%2FPhotosynthesis?printable=yes", false)]
    public void ScopedPermissionGate_DoesNotTrustArbitraryUrls(string url, bool expected)
    {
        var source = new SourceDocument
        {
            Title = "Biology/Photosynthesis",
            CanonicalId = "Biology/Photosynthesis",
            Url = url,
            Text = "A source containing a substantial instructional paragraph on plants converting solar radiation.",
            ProviderId = "wikibooks-text",
            Kind = SourceKind.WikibooksPage,
            Rights = ReviewedSourceRights.WikibooksPageText
        };

        Assert.Equal(expected, SourcePermissionGate.Evaluate(source).Allowed);
    }

    [Fact]
    public void OpenStax_IsNeverEligibleForModelIngestion()
    {
        var source = new SourceDocument
        {
            Title = "Biology 2e",
            Url = "https://openstax.org/books/biology-2e/pages/1-introduction",
            Text = "OpenStax textbook material",
            ProviderId = "openstax-text",
            Kind = SourceKind.Reference,
            Rights = ReviewedSourceRights.WikibooksPageText
        };
        Assert.False(SourcePermissionGate.Evaluate(source).Allowed);
    }
}
