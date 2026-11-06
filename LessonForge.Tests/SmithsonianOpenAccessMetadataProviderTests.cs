using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public sealed class SmithsonianOpenAccessMetadataProviderTests
{
    private const string Fixture = """
        {"id":"edanmdm-nmah_12345","url":"edanmdm:nmah_12345","type":"edanmdm","title":"American Revolution militia banner","unitCode":"NMAH","content":{"descriptiveNonRepeating":{"metadata_usage":{"access":"CC0"},"online_media":{"media":[{"usage":{"access":"Usage conditions apply"},"content":"https://restricted.example/image"}]}},"freetext":{"date":[{"content":"late eighteenth century"}],"topic":[{"content":"American Revolution"}],"notes":[{"content":"Smithsonian catalog description of a colonial militia banner."}]}}}
        {"id":"edanmdm-nmah_98765","url":"edanmdm:nmah_98765","type":"edanmdm","title":"American Revolution restricted object","unitCode":"NMAH","content":{"descriptiveNonRepeating":{"metadata_usage":{"access":"Usage conditions apply"}},"freetext":{"topic":[{"content":"American Revolution"}]}}}
        {"id":"edanmdm-nasm_5555","url":"edanmdm:nasm_5555","type":"edanmdm","title":"American Revolution fake wrong museum","unitCode":"NASM","content":{"descriptiveNonRepeating":{"metadata_usage":{"access":"CC0"}}}}
        {"id":"edanmdm-nmah_67890","url":"edanmdm:nmah_67890","type":"edanmdm","title":"Industrial printing press","unitCode":"NMAH","content":{"descriptiveNonRepeating":{"metadata_usage":{"access":"CC0"}},"freetext":{"topic":[{"content":"Industrial technology"}]}}}
        """;

    private sealed class Handler(string body, string mediaType = "text/plain",
        HttpStatusCode status = HttpStatusCode.OK, string? finalUrl = null,
        bool omitRequest = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Requests.Add(request.RequestUri!);
            var reply = new HttpResponseMessage(status)
            {
                RequestMessage = omitRequest ? null : finalUrl is null
                    ? request : new HttpRequestMessage(HttpMethod.Get, finalUrl),
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
            };
            reply.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(reply);
        }
    }

    private sealed class TempCache : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lessonforge-si-tests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    [Fact]
    public async Task OnlyCc0MetadataFromMatchingUnitIsIndexed_NoMediaOrRestrictedAsset()
    {
        using var cache = new TempCache();
        var handler = new Handler(Fixture);
        var provider = new SmithsonianOpenAccessMetadataProvider(
            new HttpClient(handler), cache.Path) { MaxShards = 2 };
        var records = await provider.RetrieveAsync("American Revolution");

        var record = Assert.Single(records);
        Assert.Equal("smithsonian-openaccess-metadata", record.ProviderId);
        Assert.Equal(SourceKind.SmithsonianMetadata, record.Kind);
        Assert.Equal("CC0-1.0", record.Rights?.LicenseId);
        Assert.Equal("edanmdm:nmah_12345", record.CanonicalId);
        Assert.Equal("https://collections.si.edu/search/detail/edanmdm:nmah_12345", record.Url);
        Assert.True(SourcePermissionGate.Evaluate(record).Allowed);
        Assert.Contains("American Revolution", record.Text);
        Assert.Contains("CC0 CATALOG METADATA ONLY", record.Text);
        Assert.DoesNotContain("restricted.example", record.Text);
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Requests, req =>
        {
            Assert.Equal(SmithsonianOpenAccessMetadataProvider.BucketHost, req.Host);
            Assert.StartsWith("/metadata/edan/nmah/", req.AbsolutePath);
            Assert.EndsWith(".txt", req.AbsolutePath);
            Assert.Empty(req.Query);
        });
    }

    [Theory]
    [InlineData("art history", "/metadata/edan/saam/")]
    [InlineData("space exploration", "/metadata/edan/nasm/")]
    [InlineData("photosynthesis", "/metadata/edan/nmnhbotany/")]
    [InlineData("American Revolution", "/metadata/edan/nmah/")]
    public async Task TopicSelectsExactlyOneRecognizedMuseumUnit(string topic, string pathPrefix)
    {
        using var cache = new TempCache();
        var handler = new Handler(Fixture);
        var provider = new SmithsonianOpenAccessMetadataProvider(
            new HttpClient(handler), cache.Path) { MaxShards = 1 };
        await provider.RetrieveAsync(topic);
        Assert.Single(handler.Requests);
        Assert.StartsWith(pathPrefix, handler.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task CachedShardIsReusedWithoutNetworkAndIgnoredIfTopicDoesNotMatch()
    {
        using var cache = new TempCache();
        var handler = new Handler(Fixture);
        var provider = new SmithsonianOpenAccessMetadataProvider(
            new HttpClient(handler), cache.Path) { MaxShards = 1 };
        Assert.Single(await provider.RetrieveAsync("American Revolution"));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(await provider.RetrieveAsync("history of the Antarctic"));
        Assert.Equal(1, handler.Calls);
        Assert.True(File.Exists(System.IO.Path.Combine(cache.Path, "nmah", "00.txt")));
    }

    [Fact]
    public async Task UnrelatedRightsOrNoExplicitCc0DoesNotPass()
    {
        using var cache = new TempCache();
        var wrongRights = Fixture.Replace(
            "\"access\":\"CC0\"", "\"access\":\"Usage conditions apply\"");
        var handler = new Handler(wrongRights);
        var provider = new SmithsonianOpenAccessMetadataProvider(
            new HttpClient(handler), cache.Path) { MaxShards = 1 };
        Assert.Empty(await provider.RetrieveAsync("American Revolution"));
    }

    [Theory]
    [InlineData("https://evil.example/metadata/edan/nmah/00.txt")]
    [InlineData("http://smithsonian-open-access.s3-us-west-2.amazonaws.com/metadata/edan/nmah/00.txt")]
    [InlineData("https://smithsonian-open-access.s3-us-west-2.amazonaws.com/metadata/edan/saam/00.txt")]
    public async Task RedirectOrDowngradeFailsClosed(string finalUrl)
    {
        using var cache = new TempCache();
        var handler = new Handler(Fixture, finalUrl: finalUrl);
        var provider = new SmithsonianOpenAccessMetadataProvider(
            new HttpClient(handler), cache.Path) { MaxShards = 1 };
        Assert.Empty(await provider.RetrieveAsync("American Revolution"));
    }

    [Fact]
    public async Task NoResponseRequestMetadataOrNonTextContentIsRejected()
    {
        using var cache = new TempCache();
        var missing = new Handler(Fixture, omitRequest: true);
        Assert.Empty(await new SmithsonianOpenAccessMetadataProvider(
            new HttpClient(missing), cache.Path) { MaxShards = 1 }
            .RetrieveAsync("American Revolution"));

        var html = new Handler(Fixture, mediaType: "text/html");
        Assert.Empty(await new SmithsonianOpenAccessMetadataProvider(
            new HttpClient(html), cache.Path) { MaxShards = 1 }
            .RetrieveAsync("American Revolution"));
    }

    [Fact]
    public async Task MissingShardDoesNotBreakOptionalProvider()
    {
        using var cache = new TempCache();
        var unavailable = new Handler("{}", status: HttpStatusCode.ServiceUnavailable);
        var records = await new SmithsonianOpenAccessMetadataProvider(
            new HttpClient(unavailable), cache.Path) { MaxShards = 2 }
            .RetrieveAsync("American Revolution");
        Assert.Empty(records);
        Assert.Equal(2, unavailable.Calls);
    }

    [Fact]
    public async Task MalformedLinesAreSkippedButValidCc0RecordSurvives()
    {
        using var cache = new TempCache();
        var handler = new Handler("not-json\n" + Fixture);
        var records = await new SmithsonianOpenAccessMetadataProvider(
            new HttpClient(handler), cache.Path) { MaxShards = 1 }
            .RetrieveAsync("American Revolution");
        Assert.Single(records);
    }

    [Fact]
    public async Task RestrictedMediaNeverGrantsRightsToCollectionObject()
    {
        var eligibleMetadata = new SourceDocument
        {
            Title = "Smithsonian Open Access catalog record",
            Url = "https://collections.si.edu/search/detail/edanmdm:nmah_12345",
            CanonicalId = "edanmdm:nmah_12345",
            ProviderId = "smithsonian-openaccess-metadata",
            Kind = SourceKind.SmithsonianMetadata,
            Text = "Catalog metadata only, without media or external page content.",
            Rights = ReviewedSourceRights.SmithsonianCc0Metadata
        };
        Assert.True(SourcePermissionGate.Evaluate(eligibleMetadata).Allowed);
        Assert.False(SourcePermissionGate.Evaluate(new SourceDocument
        {
            Title = eligibleMetadata.Title, Url = eligibleMetadata.Url,
            CanonicalId = eligibleMetadata.CanonicalId,
            ProviderId = eligibleMetadata.ProviderId, Text = "Image data.",
            Kind = SourceKind.Reference, Rights = eligibleMetadata.Rights
        }).Allowed);
    }
}
