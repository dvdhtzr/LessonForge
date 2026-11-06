using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public sealed class LocLawmakingCatalogProviderTests
{
    private const string Fixture = """
        {
          "results": [
            {
              "id":"http://www.loc.gov/item/12345678/",
              "title":"Congressional debate on a historical constitutional question",
              "description":["Catalog note about the congressional record and its consideration of constitutional authority."],
              "date":"1850",
              "partof":["A Century of Lawmaking for a New Nation: U.S. Congressional Documents and Debates, 1774-1875"],
              "access_restricted":false
            },
            {
              "id":"https://www.loc.gov/item/87654321/",
              "title":"A second congressional document with a restricted rights advisory",
              "description":["A catalog note for an item which must not be ingested even if it appears in the collection."],
              "partof":["A Century of Lawmaking for a New Nation: U.S. Congressional Documents and Debates, 1774-1875"],
              "rights_advisory":["Permission required; copyrighted contents"]
            },
            {
              "id":"https://www.loc.gov/item/44444444/",
              "title":"An item from an unrelated archival collection",
              "description":["A separate collection item that must never inherit this public-domain rights decision."],
              "partof":["Unreviewed Photographs Collection"]
            },
            {
              "id":"https://evil.example/item/12345678/",
              "title":"Attacker controlled URL posing as a congressional document",
              "description":["An attacker supplied URL embedded in a JSON response is not a trusted reference."],
              "partof":["A Century of Lawmaking for a New Nation"]
            },
            {
              "id":"https://www.loc.gov/item/77777777/",
              "title":"Congressional proceedings on civil liberty and citizenship",
              "summary":["Catalog summary concerning a historical congressional discussion of citizenship and civil liberties."],
              "partof":["A Century of Lawmaking for a New Nation"]
            }
          ]
        }
        """;

    private sealed class Handler(string json, string mediaType = "application/json",
        HttpStatusCode status = HttpStatusCode.OK, string? finalUrl = null,
        bool omitRequest = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = omitRequest ? null : finalUrl is null
                    ? request : new HttpRequestMessage(HttpMethod.Get, finalUrl),
                Content = new StringContent(json, Encoding.UTF8, mediaType)
            });
        }
    }

    [Fact]
    public async Task PublicDomainCatalogRecords_AreNormalizedAndCitable()
    {
        var handler = new Handler(Fixture);
        var provider = new LocLawmakingCatalogProvider(new HttpClient(handler));
        var records = await provider.RetrieveAsync("United States constitution");

        Assert.Equal(2, records.Count);
        Assert.All(records, record =>
        {
            Assert.Equal("loc-century-of-lawmaking", record.ProviderId);
            Assert.Equal(SourceKind.LocCongressionalCatalog, record.Kind);
            Assert.Equal("US-PD", record.Rights?.LicenseId);
            Assert.True(SourcePermissionGate.Evaluate(record).Allowed);
            Assert.Contains("NOT the document's full text", record.Text);
            Assert.DoesNotContain("evil.example", record.Url);
        });
        Assert.Equal("https://www.loc.gov/item/12345678/", records[0].Url);
        Assert.Equal("12345678", records[0].CanonicalId);
        Assert.Contains("1850", records[0].Text);
        Assert.Contains("consideration of constitutional authority", records[0].Text);
        Assert.Equal("https://www.loc.gov/item/77777777/", records[1].Url);
        Assert.Equal(1, handler.Calls);
        Assert.NotNull(handler.Requested);
        Assert.Equal("/collections/century-of-lawmaking/", handler.Requested!.AbsolutePath);
        Assert.Contains("q=United%20States%20constitution", handler.Requested.Query);
        Assert.Contains("fo=json", handler.Requested.Query);
    }

    [Fact]
    public async Task UnrelatedTopicDoesNotContactLibrary()
    {
        var handler = new Handler(Fixture);
        var records = await new LocLawmakingCatalogProvider(new HttpClient(handler))
            .RetrieveAsync("Photosynthesis in freshwater algae");
        Assert.Empty(records);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("https://different.example/collections/century-of-lawmaking/")]
    [InlineData("http://www.loc.gov/collections/century-of-lawmaking/")]
    [InlineData("https://www.loc.gov/collections/another-collection/")]
    public async Task UnexpectedEffectiveEndpointIsRejected(string finalUrl)
    {
        var handler = new Handler(Fixture, finalUrl: finalUrl);
        Assert.Empty(await new LocLawmakingCatalogProvider(new HttpClient(handler))
            .RetrieveAsync("congressional history"));
    }

    [Fact]
    public async Task MissingResponseRequestMetadata_IsRejected()
    {
        var handler = new Handler(Fixture, omitRequest: true);
        Assert.Empty(await new LocLawmakingCatalogProvider(new HttpClient(handler))
            .RetrieveAsync("congressional history"));
    }

    [Fact]
    public async Task InvalidTypesContentSizeAndStatus_FailClosed()
    {
        var html = new Handler(Fixture, mediaType: "text/html");
        Assert.Empty(await new LocLawmakingCatalogProvider(new HttpClient(html))
            .RetrieveAsync("congress"));

        var malformed = new Handler("""{"results":"not an array"}""");
        Assert.Empty(await new LocLawmakingCatalogProvider(new HttpClient(malformed))
            .RetrieveAsync("congress"));

        var unavailable = new Handler("{}", status: HttpStatusCode.ServiceUnavailable);
        Assert.Empty(await new LocLawmakingCatalogProvider(new HttpClient(unavailable))
            .RetrieveAsync("congress"));

        var oversized = new Handler(new string('x', 5000));
        Assert.Empty(await new LocLawmakingCatalogProvider(new HttpClient(oversized))
        {
            MaxResponseBytes = 2048
        }.RetrieveAsync("congress"));
    }

    [Fact]
    public async Task RestrictedOrWrongCollectionNeverIngested()
    {
        var onlyRestricted = new Handler(Fixture.Replace(
            "\"access_restricted\":false", "\"access_restricted\":true").Replace(
            "\"partof\":[\"A Century of Lawmaking for a New Nation\"]",
            "\"partof\":[\"Unreviewed Collection\"]"));
        var records = await new LocLawmakingCatalogProvider(new HttpClient(onlyRestricted))
            .RetrieveAsync("congressional history");
        Assert.Empty(records);
    }

    [Fact]
    public async Task AdapterBoundsRecordsAndRejectsInvalidConfiguration()
    {
        var handler = new Handler(Fixture);
        var records = await new LocLawmakingCatalogProvider(new HttpClient(handler))
        {
            MaxResults = 1
        }.RetrieveAsync("congress");
        Assert.Single(records);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new LocLawmakingCatalogProvider(new HttpClient(handler))
            {
                MaxResults = 0
            }.RetrieveAsync("congress");
        });
    }

    [Fact]
    public async Task QueryTextIsEncodedAndCannotChangeApiEndpoint()
    {
        var handler = new Handler(Fixture);
        await new LocLawmakingCatalogProvider(new HttpClient(handler))
            .RetrieveAsync("congress &fo=xml&c=9999");
        Assert.Equal("/collections/century-of-lawmaking/", handler.Requested?.AbsolutePath);
        Assert.Contains("%26fo%3Dxml%26c%3D9999", handler.Requested?.Query);
    }
}
