using System.Net;
using System.Text;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public sealed class UsgsEarthquakeProviderTests
{
    private const string Fixture = """
        {
          "type":"FeatureCollection",
          "metadata":{"count":3},
          "features":[
            {"type":"Feature","id":"us1234","properties":{
              "type":"earthquake","mag":6.1,"place":"100 km SE of Example City",
              "time":1760000000000,"status":"reviewed",
              "url":"https://evil.example/ignore-this-url"
            }},
            {"type":"Feature","id":"us5678","properties":{
              "type":"earthquake","mag":4.8,"place":"Near Test Island",
              "time":1760003600000,"status":"automatic"
            }},
            {"type":"Feature","id":"../evil","properties":{
              "type":"earthquake","mag":9,"place":"malicious",
              "time":1760007200000
            }}
          ]
        }
        """;

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "application/json") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(UsgsEarthquakeProvider.FeedUrl, request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
        }
    }

    [Fact]
    public async Task StructuredEvents_ProduceRealCitableNormalizedEvidence()
    {
        var handler = new Handler(Fixture);
        var provider = new UsgsEarthquakeProvider(new HttpClient(handler));
        var documents = await provider.RetrieveAsync("How do earthquakes work?");

        Assert.Equal(2, documents.Count);
        Assert.All(documents, doc =>
        {
            Assert.Equal(SourceKind.UsgsEarthquake, doc.Kind);
            Assert.Equal("usgs-earthquakes", doc.ProviderId);
            Assert.True(SourcePermissionGate.Evaluate(doc).Allowed);
            Assert.Contains("https://earthquake.usgs.gov/earthquakes/eventpage/", doc.Url);
            Assert.DoesNotContain("evil.example", doc.Text);
            Assert.Contains("UTC", doc.Text);
        });
        Assert.Equal("us1234", documents[0].CanonicalId);
        Assert.Equal("https://earthquake.usgs.gov/earthquakes/eventpage/us1234", documents[0].Url);
        Assert.Contains("magnitude 6.1", documents[0].Text);
        Assert.Contains("100 km SE of Example City", documents[0].Text);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnrelatedSubjects_DoNotCallProvider()
    {
        var handler = new Handler(Fixture);
        var provider = new UsgsEarthquakeProvider(new HttpClient(handler));
        Assert.Empty(await provider.RetrieveAsync("History of the Protestant Reformation"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NonJsonAndOversizedResponses_AreNotIndexed()
    {
        var html = new Handler("<html>No data</html>", mediaType: "text/html");
        Assert.Empty(await new UsgsEarthquakeProvider(new HttpClient(html))
            .RetrieveAsync("earthquake"));
        var oversize = new Handler(new string('X', 5000));
        Assert.Empty(await new UsgsEarthquakeProvider(new HttpClient(oversize))
        {
            MaxResponseBytes = 1024
        }.RetrieveAsync("earthquake"));
    }

    [Fact]
    public async Task TransientHttpError_ReturnsNoDocuments()
    {
        var handler = new Handler("{}", HttpStatusCode.ServiceUnavailable);
        var sources = await new UsgsEarthquakeProvider(new HttpClient(handler))
            .RetrieveAsync("earthquake");
        Assert.Empty(sources);
    }

    [Fact]
    public async Task OnlyEarthquakeItems_AndBoundedCounts_AreAccepted()
    {
        var handler = new Handler(Fixture.Replace(
            "\"type\":\"earthquake\",\"mag\":4.8",
            "\"type\":\"quarry blast\",\"mag\":4.8"));
        var sources = await new UsgsEarthquakeProvider(new HttpClient(handler))
        {
            MaxEvents = 1
        }.RetrieveAsync("earthquake"));
        Assert.Single(sources);
        Assert.Equal("us1234", sources[0].CanonicalId);
    }

    [Fact]
    public async Task InvalidFeedShape_IsNotTreatedAsEvidence()
    {
        var handler = new Handler("""{"type":"SomethingElse","features":[]}""");
        var sources = await new UsgsEarthquakeProvider(new HttpClient(handler))
            .RetrieveAsync("earthquakes");
        Assert.Empty(sources);
    }
}
