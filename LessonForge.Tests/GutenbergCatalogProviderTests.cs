using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public sealed class GutenbergCatalogProviderTests
{
    private const string Fixture =
        "Text#,Type,Issued,Title,Language,Authors,Subjects,LoCC,Bookshelves\r\n" +
        "1342,Text,1998-06-01,\"Pride and Prejudice\",en,\"Austen, Jane\",\"English fiction; Social class\",PR,\"Classics\"\r\n" +
        "3526,Text,2004-03-04,\"History of the Protestant Reformation\",en,\"D'Aubigné, J. H. Merle\",\"Reformation -- History; Protestant churches\",BR,\"History; Religion\"\r\n" +
        "2001,Text,2001-04-02,\"The French Revolution, Vol. 1\",en,\"Carlyle, Thomas\",\"France -- History -- Revolution\",DC,\"History\"\r\n" +
        "9090,Sound,2008-02-02,History of the Protestant Reformation,en,Audio,History,BR,History\r\n" +
        "8080,Text,2009-02-02,History of the Protestant Reformation,fr,\"Auteur, Exemple\",Histoire,BR,History\r\n" +
        "8081,Text,2010-02-02,\"Poems, Songs and Sonnets\",en,\"Author, Imaginary\",Poetry,PR,Poetry\r\n";

    private sealed class Handler(byte[] body, HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "text/csv", string? finalUrl = null,
        bool omitRequest = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = omitRequest ? null : finalUrl is null
                    ? request : new HttpRequestMessage(HttpMethod.Get, finalUrl),
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue(mediaType) }
                }
            });
        }
    }

    private sealed class TempCache : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lessonforge-pg-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }


    [Fact]
    public async Task OfficialCsv_ProducesBibliographicSourcesButNeverInventsBookTextRights()
    {
        using var cache = new TempCache();
        var handler = new Handler(Encoding.UTF8.GetBytes(Fixture));
        var provider = new GutenbergCatalogProvider(new HttpClient(handler), cache.Path);
        var books = await provider.RetrieveAsync("Protestant Reformation");

        var result = Assert.Single(books);
        Assert.Equal("gutenberg-catalog", result.ProviderId);
        Assert.Equal(SourceKind.GutenbergCatalog, result.Kind);
        Assert.Equal("3526", result.CanonicalId);
        Assert.Equal("https://www.gutenberg.org/ebooks/3526", result.Url);
        Assert.Equal("PG-CATALOG-PD", result.Rights?.LicenseId);
        Assert.True(SourcePermissionGate.Evaluate(result).Allowed);
        Assert.Contains("NOT the ebook's text", result.Text);
        Assert.Contains("Reformation -- History", result.Text);
        Assert.Contains("NOT the original publication date", result.Text);
        Assert.Contains("D'Aubigné", result.Text);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(GutenbergCatalogProvider.FeedUrl, handler.LastUri?.ToString());
    }

    [Fact]
    public async Task QuotedCsvAndMultipleBooks_AreRankedWithoutScraping()
    {
        using var cache = new TempCache();
        var csv = Fixture + "5555,Text,2001-01-01,\"French Revolution, Volume 2\",en,\"Fiction, A. \"\"Strange\"\" Writer\",\"France -- History, 1789\",DC,\"French History\"\r\n";
        var handler = new Handler(Encoding.UTF8.GetBytes(csv));
        var records = await new GutenbergCatalogProvider(new HttpClient(handler), cache.Path)
        {
            MaxResults = 3
        }.RetrieveAsync("French Revolution");

        Assert.Equal(2, records.Count);
        Assert.All(records, book => Assert.Contains("French Revolution", book.Title));
        Assert.Contains(records, book => book.Text.Contains("Strange"));
        Assert.DoesNotContain(records, book => book.CanonicalId == "8080" || book.CanonicalId == "9090");
    }

    [Fact]
    public async Task CachedFeedIsReusedAcrossProviderInstances_WithoutAnotherDownload()
    {
        using var cache = new TempCache();
        var handler = new Handler(Encoding.UTF8.GetBytes(Fixture));
        var first = new GutenbergCatalogProvider(new HttpClient(handler), cache.Path);
        Assert.Single(await first.RetrieveAsync("Pride and Prejudice"));
        Assert.Equal(1, handler.Calls);

        var second = new GutenbergCatalogProvider(new HttpClient(handler), cache.Path);
        Assert.Single(await second.RetrieveAsync("Pride and Prejudice"));
        Assert.Equal(1, handler.Calls);
        Assert.True(File.Exists(System.IO.Path.Combine(cache.Path, "pg_catalog.csv")));
    }

    [Fact]
    public async Task UnsupportedOrIrrelevantTopicsDoNotContributeEbooks()
    {
        using var cache = new TempCache();
        var handler = new Handler(Encoding.UTF8.GetBytes(Fixture));
        var provider = new GutenbergCatalogProvider(new HttpClient(handler), cache.Path);
        Assert.Empty(await provider.RetrieveAsync("and the for"));
        Assert.Equal(0, handler.Calls);

        Assert.Empty(await provider.RetrieveAsync("photosynthesis chlorophyll"));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://evil.example/cache/epub/feeds/pg_catalog.csv")]
    [InlineData("http://www.gutenberg.org/cache/epub/feeds/pg_catalog.csv")]
    [InlineData("https://www.gutenberg.org/ebooks/")]
    public async Task RedirectedOrDowngradedFeed_IsRejected(string finalUrl)
    {
        using var cache = new TempCache();
        var handler = new Handler(Encoding.UTF8.GetBytes(Fixture), finalUrl: finalUrl);
        var result = await new GutenbergCatalogProvider(new HttpClient(handler), cache.Path)
            .RetrieveAsync("Pride and Prejudice");
        Assert.Empty(result);
        Assert.False(File.Exists(System.IO.Path.Combine(cache.Path, "pg_catalog.csv")));
    }

    [Fact]
    public async Task MissingEffectiveRequestMetadataFailsClosed()
    {
        using var cache = new TempCache();
        var handler = new Handler(Encoding.UTF8.GetBytes(Fixture), omitRequest: true);
        Assert.Empty(await new GutenbergCatalogProvider(new HttpClient(handler), cache.Path)
            .RetrieveAsync("Protestant Reformation"));
    }

    [Fact]
    public async Task IncorrectContentTypeOrStatus_IsNotCatalogEvidence()
    {
        using var cache = new TempCache();
        var html = new Handler(Encoding.UTF8.GetBytes(Fixture), mediaType: "text/html");
        Assert.Empty(await new GutenbergCatalogProvider(new HttpClient(html), cache.Path)
            .RetrieveAsync("Protestant Reformation"));
        var unavailable = new Handler(Encoding.UTF8.GetBytes(Fixture), status: HttpStatusCode.TooManyRequests);
        Assert.Empty(await new GutenbergCatalogProvider(new HttpClient(unavailable), cache.Path)
            .RetrieveAsync("Protestant Reformation"));
    }

    [Fact]
    public async Task CsvDownloadSizeCap_AndSchemaValidation_AreEnforced()
    {
        using var cache = new TempCache();
        var oversized = new Handler(Encoding.UTF8.GetBytes(Fixture + new string('x', 7000)));
        var result = await new GutenbergCatalogProvider(new HttpClient(oversized), cache.Path)
        {
            MaxDownloadBytes = 2048
        }.RetrieveAsync("Protestant Reformation");
        Assert.Empty(result);

        using var cache2 = new TempCache();
        var invalid = new Handler(Encoding.UTF8.GetBytes(
            "ID,Title\n1,A book not following the official Gutenberg catalog schema\n"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await new GutenbergCatalogProvider(new HttpClient(invalid), cache2.Path)
                .RetrieveAsync("Protestant Reformation");
        });
    }

    [Fact]
    public async Task MissingRequiredColumnsCannotBecomeSourceEvidence()
    {
        using var cache = new TempCache();
        var handler = new Handler(Encoding.UTF8.GetBytes("ID,Title\n1,Unknown Book\n"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await new GutenbergCatalogProvider(new HttpClient(handler), cache.Path)
                .RetrieveAsync("Unknown Book");
        });
    }

    [Fact]
    public async Task SourceGateRejectsEbookTextAndArbitraryLandingPages()
    {
        var doc = new SourceDocument
        {
            Title = "Catalog record",
            Url = "https://www.gutenberg.org/ebooks/1342",
            CanonicalId = "1342",
            ProviderId = "gutenberg-catalog",
            Text = "Bibliographic metadata about a book; not its text.",
            Kind = SourceKind.GutenbergCatalog,
            Rights = ReviewedSourceRights.GutenbergCatalogMetadata
        };
        Assert.True(SourcePermissionGate.Evaluate(doc).Allowed);

        var bookContent = new SourceDocument
        {
            Title = doc.Title,
            Url = doc.Url,
            CanonicalId = doc.CanonicalId,
            ProviderId = doc.ProviderId,
            Text = "Actual ebook body is not covered by this permission.",
            Kind = SourceKind.Reference,
            Rights = doc.Rights
        };
        Assert.False(SourcePermissionGate.Evaluate(bookContent).Allowed);

        var forgedId = new SourceDocument
        {
            Title = doc.Title, Url = "https://www.gutenberg.org/ebooks/999",
            CanonicalId = doc.CanonicalId,
            ProviderId = doc.ProviderId, Text = doc.Text,
            Kind = doc.Kind, Rights = doc.Rights
        };
        Assert.False(SourcePermissionGate.Evaluate(forgedId).Allowed);
    }
}
