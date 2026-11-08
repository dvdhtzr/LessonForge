using System.Net;
using System.Text;
using System.Text.Json;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public sealed class PerspectiveTopicDetectorTests
{
    private static SourceDocument Article(string title, string text) => new()
    {
        Title = title,
        Url = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')),
        Text = text,
        CanonicalId = title,
        ProviderId = "wikipedia-text",
        Kind = SourceKind.WikipediaArticle,
        Rights = ReviewedSourceRights.WikipediaArticleText
    };

    [Fact]
    public void StanfordPrisonExperiment_OffersOptionalReviewOnMultipleDisputeSignals()
    {
        var result = PerspectiveTopicDetector.Evaluate(Article("Stanford prison experiment",
            """
            The Stanford prison experiment examined how participants behaved in assigned
            prison roles. Later critics challenged the validity of the original conclusions.
            The experiment prompted ethical concerns about the treatment of participants.
            Critics questioned methodological flaws and the reproducibility of some findings.
            """));

        Assert.True(result.Suggested);
        Assert.Equal("suggested", result.Status);
        Assert.Equal("Stanford prison experiment", result.SourceTitle);
        Assert.StartsWith("https://en.wikipedia.org/", result.SourceUrl);
    }

    [Theory]
    [InlineData("Gravity", "Gravity is the attraction between masses. In general relativity, curved spacetime describes gravitational interactions. Scientists study gravitational waves and planetary motion.")]
    [InlineData("Boiling point", "The boiling point of water is the temperature at which its vapor pressure equals the surrounding pressure. The boiling point changes with atmospheric pressure, and at standard pressure it is about 100 degrees Celsius.")]
    [InlineData("Photosynthesis", "Photosynthesis uses sunlight to support chemical processes in plants. In plants, chlorophyll absorbs light and sugars are synthesized.")]
    public void WellEstablishedNaturalPhenomenon_DoesNotOfferDebate(string title, string text)
    {
        var result = PerspectiveTopicDetector.Evaluate(Article(title, text));

        Assert.False(result.Suggested);
        Assert.Equal("not_suggested", result.Status);
    }

    [Fact]
    public void OneIncidentalControversyWord_DoesNotOfferAnAutomaticReview()
    {
        var result = PerspectiveTopicDetector.Evaluate(Article("Gravity",
            "A historical controversy is mentioned in the introduction, but the " +
            "article explains gravity as an established physical phenomenon."));
        Assert.False(result.Suggested);
    }

    [Fact]
    public void UnreviewedExternalSource_IsNotAllowedToTriggerSuggestion()
    {
        var source = new SourceDocument
        {
            Title = "Unreviewed blog",
            Url = "https://example.org/blog",
            Text = "Controversy and ethical concerns about disputed results.",
            ProviderId = "unreviewed-blog",
            Kind = SourceKind.Reference
        };
        Assert.Equal("unavailable", PerspectiveTopicDetector.Evaluate(source).Status);
    }

    private sealed class ArticleHandler(bool wrongIdentity = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            object result;
            if (query.Contains("list=search", StringComparison.Ordinal))
            {
                result = new
                {
                    query = new
                    {
                        search = new[] { new { title = wrongIdentity ? "Jesús Carroza" : "Stanford prison experiment" } }
                    }
                };
            }
            else
            {
                var title = wrongIdentity ? "Jesús Carroza" : "Stanford prison experiment";
                result = new
                {
                    query = new
                    {
                        pages = new Dictionary<string, object>
                        {
                            ["123"] = new
                            {
                                title,
                                canonicalurl = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')),
                                extract = "Some researchers disputed the methodology. The study became " +
                                    "controversial and prompted ethical concerns about the treatment of people."
                            }
                        }
                    }
                };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(result), Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task PreflightReadsOneExactArticleAndSuggestsOptionalAnalysis()
    {
        var handler = new ArticleHandler();
        var result = await new InitialArticlePerspectiveProbe(
            new WikipediaClient(new HttpClient(handler)))
            .CheckAsync("Stanford prison experiment");

        Assert.True(result.Suggested);
        Assert.Equal("Stanford prison experiment", result.SourceTitle);
        Assert.Equal(1, handler.Calls); // Direct exact title; no secondary crawl.
    }

    [Fact]
    public async Task PreflightDoesNotSubstituteSimilarlyNamedPerson()
    {
        var handler = new ArticleHandler(wrongIdentity: true);
        var result = await new InitialArticlePerspectiveProbe(
            new WikipediaClient(new HttpClient(handler)))
            .CheckAsync("Carmen Carrozza");

        Assert.False(result.Suggested);
        Assert.Equal("unavailable", result.Status);
        Assert.Equal(2, handler.Calls); // One direct attempt and one exact-title search.
    }

    private sealed class EscobarTopicHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var q = Uri.UnescapeDataString(request.RequestUri!.Query);
            var json = q.Contains("list=search", StringComparison.Ordinal)
                ? """
                  {"query":{"search":[
                    {"title":"Pablo Escobar"},
                    {"title":"Hippopotamuses in Colombia"}
                  ]}}
                  """
                : q.Contains("titles=Hippopotamuses in Colombia", StringComparison.Ordinal)
                    ? """
                      {"query":{"pages":{"123":{
                        "title":"Hippopotamuses in Colombia",
                        "canonicalurl":"https://en.wikipedia.org/wiki/Hippopotamuses_in_Colombia",
                        "extract":"Hippopotamuses were kept by Pablo Escobar in Colombia. Conservationists debated how to manage them after they escaped. The resulting invasive animal population raised ethical concerns and controversy."
                      }}}}
                      """
                    : """{"query":{"pages":{"-1":{"missing":true}}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task PossessiveTopicPreviewFollowsSameVerifiedSubjectResolutionAsResearch()
    {
        var preview = new InitialArticlePerspectiveProbe(
            new WikipediaClient(new HttpClient(new EscobarTopicHandler())));

        var result = await preview.CheckAsync("Pablo Escobar's Hippos");

        Assert.True(result.Suggested);
        Assert.Equal("Hippopotamuses in Colombia", result.SourceTitle);
        Assert.Equal("https://en.wikipedia.org/wiki/Hippopotamuses_in_Colombia", result.SourceUrl);
    }

    [Fact]
    public async Task EmptyTopicDoesNotIssueNetworkRequests()
    {
        var handler = new ArticleHandler();
        var result = await new InitialArticlePerspectiveProbe(
            new WikipediaClient(new HttpClient(handler))).CheckAsync(" ");

        Assert.Equal("unavailable", result.Status);
        Assert.Equal(0, handler.Calls);
    }
}
