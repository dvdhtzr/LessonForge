using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public sealed class SourcePermissionAndRegistryTests
{
    private static SourceDocument Document(SourceRights? rights = null) => new()
    {
        Title = "A USGS observation",
        Url = "https://earthquake.usgs.gov/earthquakes/eventpage/us123456",
        CanonicalId = "us123456",
        ProviderId = "usgs-earthquakes",
        Kind = SourceKind.UsgsEarthquake,
        Rights = rights,
        Text = "USGS earthquake observation at a specific location and date, with magnitude."
    };

    [Fact]
    public void MissingRightsOrUnknownPermission_FailsClosed()
    {
        Assert.False(SourcePermissionGate.Evaluate(Document()).Allowed);
        Assert.False(SourcePermissionGate.Evaluate(Document(new SourceRights
        {
            LicenseId = "US-PD",
            PolicyUrl = "https://www.usgs.gov/information-policies-and-instructions/copyrights-and-credits",
            ReviewedOn = new DateOnly(2026, 11, 6),
            Scope = "USGS events only"
        })).Allowed);
    }

    [Fact]
    public void ExplicitAiProhibition_OverridesOtherPermissions()
    {
        var doc = Document(ReviewedSourceRights.UsgsEarthquakeEvents with { ExplicitAiProhibition = true });
        var result = SourcePermissionGate.Evaluate(doc);
        Assert.False(result.Allowed);
        Assert.Contains("Explicit AI", result.Reason);
    }

    [Fact]
    public void VerifiedUsgsEventIsAllowed_ArbitraryUsgsPagesAreDenied()
    {
        var doc = Document(ReviewedSourceRights.UsgsEarthquakeEvents);
        Assert.True(SourcePermissionGate.Evaluate(doc).Allowed);

        var wrongKind = new SourceDocument
        {
            Title = doc.Title, Url = doc.Url, Text = doc.Text,
            ProviderId = doc.ProviderId, Kind = SourceKind.Reference, Rights = doc.Rights
        };
        Assert.False(SourcePermissionGate.Evaluate(wrongKind).Allowed);

        var arbitraryUrl = new SourceDocument
        {
            Title = doc.Title, Url = "https://www.usgs.gov/anything",
            Text = doc.Text, ProviderId = doc.ProviderId,
            Kind = doc.Kind, Rights = doc.Rights
        };
        Assert.False(SourcePermissionGate.Evaluate(arbitraryUrl).Allowed);
    }

    [Fact]
    public void WikipediaArticleTextAllowed_ExternalReferencesDenied()
    {
        var wikipedia = new SourceDocument
        {
            Title = "Photosynthesis",
            Url = "https://en.wikipedia.org/wiki/Photosynthesis",
            Text = "A sufficiently detailed encyclopedia article extract about how plants turn light into chemical energy.",
            ProviderId = "wikipedia-text",
            Kind = SourceKind.WikipediaArticle,
            Rights = ReviewedSourceRights.WikipediaArticleText
        };
        Assert.True(SourcePermissionGate.Evaluate(wikipedia).Allowed);
        Assert.False(ReviewedSourceRights.WikipediaArticleText.MayRedistribute);
        Assert.False(ReviewedSourceRights.WikipediaArticleText.MayReproduceExcerpts);

        var refPage = new SourceDocument
        {
            Title = "External article", Url = "https://third-party.example/article",
            Text = "This is an unreviewed source page cited as a reference.",
            ProviderId = "external-reference", Kind = SourceKind.Reference
        };
        Assert.False(SourcePermissionGate.Evaluate(refPage).Allowed);
    }

    [Fact]
    public void ProviderCatalog_LeavesUnreviewedCandidatesDisabled()
    {
        var registry = new SourceRegistry();
        Assert.True(registry.Sources.Single(x => x.Id == "wikipedia-text").Enabled);
        Assert.False(registry.Sources.Single(x => x.Id == "smithsonian-openaccess-metadata").Enabled);
        Assert.False(registry.Sources.Single(x => x.Id == "gutenberg-catalog").Enabled);
        Assert.False(registry.Sources.Single(x => x.Id == "loc-century-of-lawmaking").Enabled);
        Assert.False(registry.Sources.Single(x => x.Id == "saylor").Enabled);
        Assert.False(registry.Sources.Single(x => x.Id == "usgs-earthquakes").Enabled);
        Assert.Throws<ArgumentException>(() =>
            new SourceRegistry([new FakeProvider("usgs-earthquakes", []),
                new FakeProvider("usgs-earthquakes", [])]));
        Assert.Throws<ArgumentException>(() =>
            new SourceRegistry([new FakeProvider("unreviewed", [])]));
    }

    [Fact]
    public void ScopedLoCCatalogEvidence_IsAllowedButArbitraryLoCContentIsNot()
    {
        var valid = new SourceDocument
        {
            Title = "LoC catalog: Congressional proceedings",
            Url = "https://www.loc.gov/item/12345678/",
            Text = "Library of Congress catalog descriptive note about a historical congressional record, not primary document text.",
            ProviderId = "loc-century-of-lawmaking",
            Kind = SourceKind.LocCongressionalCatalog,
            CanonicalId = "12345678",
            Rights = ReviewedSourceRights.LocCenturyOfLawmakingCatalog
        };
        Assert.True(SourcePermissionGate.Evaluate(valid).Allowed);
        Assert.Equal("US-PD", valid.Rights?.LicenseId);

        var unreviewed = new SourceDocument
        {
            Title = valid.Title, Url = "https://www.loc.gov/collections/unreviewed/",
            Text = valid.Text, ProviderId = valid.ProviderId, Kind = valid.Kind,
            Rights = valid.Rights
        };
        Assert.False(SourcePermissionGate.Evaluate(unreviewed).Allowed);

        var wrongKind = new SourceDocument
        {
            Title = valid.Title, Url = valid.Url, Text = valid.Text,
            ProviderId = valid.ProviderId, Kind = SourceKind.Reference,
            Rights = valid.Rights
        };
        Assert.False(SourcePermissionGate.Evaluate(wrongKind).Allowed);

        var blocked = new SourceDocument
        {
            Title = valid.Title, Url = valid.Url, Text = valid.Text,
            ProviderId = valid.ProviderId, Kind = valid.Kind,
            Rights = valid.Rights with { ExplicitAiProhibition = true }
        };
        Assert.False(SourcePermissionGate.Evaluate(blocked).Allowed);

        var badPolicy = new SourceDocument
        {
            Title = valid.Title, Url = valid.Url, Text = valid.Text,
            ProviderId = valid.ProviderId, Kind = valid.Kind,
            Rights = valid.Rights with { PolicyUrl = "https://www.loc.gov/legal/" }
        };
        Assert.False(SourcePermissionGate.Evaluate(badPolicy).Allowed);

        var registry = new SourceRegistry([
            new FakeProvider("loc-century-of-lawmaking", [valid])]);
        Assert.True(registry.Sources.Single(x => x.Id == "loc-century-of-lawmaking").Enabled);
    }

    [Fact]
    public async Task Registry_BlocksUnlicensedAndForeignProviderItems()
    {
        var allowed = Document(ReviewedSourceRights.UsgsEarthquakeEvents);
        var unlicensed = Document();
        var wrongId = new SourceDocument
        {
            Title = allowed.Title, Url = allowed.Url, Text = allowed.Text,
            ProviderId = "not-usgs", Kind = allowed.Kind, Rights = allowed.Rights
        };
        var registry = new SourceRegistry(
            [new FakeProvider("usgs-earthquakes", [unlicensed, wrongId, allowed])]);
        var result = await registry.RetrieveEligibleAsync("earthquakes");
        Assert.Single(result);
        Assert.Same(allowed, result[0]);
    }

    [Fact]
    public async Task OptionalProviderFailureIsIsolated_ButCancellationPropagates()
    {
        var registry = new SourceRegistry([new ThrowingProvider()]);
        Assert.Empty(await registry.RetrieveEligibleAsync("earthquakes"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await registry.RetrieveEligibleAsync("earthquakes", ct: cts.Token);
        });
    }

    private sealed class FakeProvider(string id, IReadOnlyList<SourceDocument> result)
        : IEducationalSourceProvider
    {
        public string Id => id;
        public bool SupportsTopic(string topic) => true;
        public Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
            string topic, CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class ThrowingProvider : IEducationalSourceProvider
    {
        public string Id => "usgs-earthquakes";
        public bool SupportsTopic(string topic) => true;
        public Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
            string topic, CancellationToken ct = default)
            => throw new HttpRequestException("Simulated provider failure");
    }
}
