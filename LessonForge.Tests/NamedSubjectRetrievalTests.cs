using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

/// <summary>
/// Regression: the learner asked for Carmen Carrozza, but a fuzzy Wikipedia
/// result about Jesús Carroza previously displaced the actual named subject.
/// The test uses an offline MediaWiki fixture with the WRONG hit ranked first.
/// </summary>
public sealed class NamedSubjectRetrievalTests
{
    private sealed class ConfusableWikipediaHandler(bool wrongDirect = false, bool omitDirect = false)
        : HttpMessageHandler
    {
        public int SearchRequests { get; private set; }
        public int LinkedRequests { get; private set; }
        public int ExternalRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            string json;

            if (query.Contains("list=search", StringComparison.Ordinal))
            {
                SearchRequests++;
                json = """
                    {"query":{"search":[{"title":"Jesús Carroza"},{"title":"Carmen Carrozzano"}]}}
                    """;
            }
            else if (query.Contains("prop=extracts", StringComparison.Ordinal))
            {
                if (query.Contains("titles=Carmen Carrozza", StringComparison.OrdinalIgnoreCase))
                    json = omitDirect ? """{"query":{"pages":{"-1":{"missing":true}}}}""" :
                        wrongDirect ? JesusArticle() : CarmenArticle();
                else if (query.Contains("titles=Jesús Carroza", StringComparison.Ordinal))
                    json = JesusArticle();
                else
                    json = """{"query":{"pages":{"-1":{"missing":true}}}}""";
            }
            else if (query.Contains("prop=links", StringComparison.Ordinal))
            {
                LinkedRequests++;
                json = """{"query":{"pages":{"1":{"links":[{"title":"Jesús Carroza"}]}}}}""";
            }
            else if (query.Contains("prop=extlinks", StringComparison.Ordinal))
            {
                ExternalRequests++;
                json = """{"query":{"pages":{"1":{"extlinks":[]}}}}""";
            }
            else
                json = """{"query":{"pages":{}}}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        private static string CarmenArticle() => """
            {"query":{"pages":{"1":{"title":"Carmen Carrozza",
            "canonicalurl":"https://en.wikipedia.org/wiki/Carmen_Carrozza",
            "extract":"Carmen Carrozza was an Italian-American concert accordionist. He studied with Pietro Deiro and performed as a soloist with major American orchestras. His work helped establish the accordion as a concert instrument."}}}}
            """;

        private static string JesusArticle() => """
            {"query":{"pages":{"2":{"title":"Jesús Carroza",
            "canonicalurl":"https://en.wikipedia.org/wiki/Jes%C3%BAs_Carroza",
            "extract":"Jesús Carroza Rodríguez is a Spanish actor. He starred in 7 Virgins and received the Goya Award for Best New Actor."}}}}
            """;
    }

    private sealed class ForbiddenExternalFetch : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Unreviewed reference content must not be fetched.");
    }

    private sealed class CatalogFixture(string title, string text) : IEducationalSourceProvider
    {
        public string Id => "gutenberg-catalog";
        public bool SupportsTopic(string topic) => true;

        public Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
            string topic, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SourceDocument>>(
            [
                new SourceDocument
                {
                    Title = title,
                    Text = text,
                    Url = "https://www.gutenberg.org/ebooks/123",
                    CanonicalId = "123",
                    ProviderId = Id,
                    Kind = SourceKind.GutenbergCatalog,
                    Rights = ReviewedSourceRights.GutenbergCatalogMetadata,
                    RetrievedAtUtc = DateTimeOffset.UtcNow
                }
            ]);
    }

    private static RetrievalService Create(
        ConfusableWikipediaHandler handler, IEducationalSourceProvider? catalog = null)
        => new(
            new WikipediaClient(new HttpClient(handler)),
            new SourceFetcher(new HttpClient(new ForbiddenExternalFetch())),
            new FakeEmbeddingGenerator(),
            new RetrievalOptions { MaxSeedArticles = 2, MaxLinkedArticles = 4 },
            registry: new SourceRegistry(catalog is null ? [] : [catalog]));

    [Theory]
    [InlineData("Carmen Carrozza")]
    [InlineData("carmen carrozza")]
    public async Task NamedArticleIsFetchedDirectly_InsteadOfFuzzyJesusCarroza(string topic)
    {
        var wiki = new ConfusableWikipediaHandler();
        var session = await Create(wiki).BuildAsync(topic);

        var source = Assert.Single(session.Sources);
        Assert.Equal("Carmen Carrozza", source.Title);
        Assert.Equal("https://en.wikipedia.org/wiki/Carmen_Carrozza", source.Url);
        Assert.DoesNotContain(session.Sources, doc => doc.Title.Contains("Jesús"));
        Assert.Single(session.Citations);
        Assert.Equal(0, wiki.SearchRequests);
        Assert.Equal(0, wiki.LinkedRequests);
        Assert.Equal(0, wiki.ExternalRequests);
        Assert.True(session.ChunkCount > 0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MissingOrWrongDirectArticle_CannotSubstituteSimilarNamedActor(
        bool wrongDirect, bool omitDirect)
    {
        var wiki = new ConfusableWikipediaHandler(wrongDirect, omitDirect);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await Create(wiki).BuildAsync("Carmen Carrozza");
        });

        Assert.Contains("requested named subject", error.Message);
        Assert.Contains("similarly named", error.Message);
        Assert.Equal(1, wiki.SearchRequests);
        Assert.Equal(0, wiki.LinkedRequests);
        Assert.Equal(0, wiki.ExternalRequests);
    }

    [Fact]
    public async Task CatalogWithOnlySimilarName_IsNotAdmittedToExactPersonIndex()
    {
        var source = new CatalogFixture(
            "Carmen Carrozzano",
            "This catalog describes Carmen Carrozzano and the actor Jesús Carroza.");
        var session = await Create(new ConfusableWikipediaHandler(), source)
            .BuildAsync("Carmen Carrozza");

        Assert.Single(session.Sources);
        Assert.Equal("Carmen Carrozza", session.Sources[0].Title);
        Assert.DoesNotContain(session.Citations, c => c.ProviderId == "gutenberg-catalog");
    }

    [Fact]
    public async Task ExactNameFromReviewedProvider_IsUsableWhenWikipediaIsMissing()
    {
        var source = new CatalogFixture(
            "Carmen Carrozza: catalog biography",
            "This official catalog describes Carmen Carrozza by his complete name.");
        var session = await Create(new ConfusableWikipediaHandler(omitDirect: true), source)
            .BuildAsync("Carmen Carrozza");

        var record = Assert.Single(session.Sources);
        Assert.Equal("gutenberg-catalog", record.ProviderId);
        Assert.True(SourcePermissionGate.Evaluate(record).Allowed);
        Assert.DoesNotContain(session.Citations, citation =>
            citation.Url.Contains("Jes", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Carmen Carrozza", true)]
    [InlineData("Jesús Carroza", false)]
    [InlineData("Carmen Carrozzano", false)]
    [InlineData("Carmen", false)]
    [InlineData("American Revolution", false)]
    public void CanonicalTitleDoesNotAcceptSimilarOrOverlappingNames(
        string actual, bool expected)
    {
        Assert.Equal(expected, TopicIdentity.SameTitle("Carmen Carrozza", actual));
    }

    [Fact]
    public void NamedSubjectsHaveExactWordBoundaryMatches()
    {
        Assert.True(TopicIdentity.IsExplicitNamedSubject("Carmen Carrozza"));
        Assert.False(TopicIdentity.IsExplicitNamedSubject("How do accordions work?"));
        Assert.True(TopicIdentity.IsShortTitleCandidate("carmen carrozza"));
        Assert.True(TopicIdentity.SameTitle("carmen carrozza", "Carmen_Carrozza"));
        Assert.True(TopicIdentity.SameTitle("The American Revolution", "American Revolution"));
        Assert.True(TopicIdentity.MentionsSubject(
            "Carmen Carrozza was an acclaimed accordionist.", "Carmen Carrozza"));
        Assert.False(TopicIdentity.MentionsSubject(
            "Carmen Carrozzano was an actor.", "Carmen Carrozza"));
        Assert.False(TopicIdentity.MentionsSubject(
            "Jesús Carroza performed in films.", "Carmen Carrozza"));
    }
}
