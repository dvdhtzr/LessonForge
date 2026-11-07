using LessonForge.Core.Rag;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LessonForge.Tests;

public sealed class SourceSelectionTests
{
    private static IConfiguration LoadCommittedLocalSettings() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Fixtures/CommittedAppsettings.Local.json", optional: false)
            .Build();

    [Fact]
    public void TrackedLocalConfig_EnablesWikibooksAndDocumentsOpenStaxAsDisabled()
    {
        var selection = SourceSelection.Load(LoadCommittedLocalSettings());

        Assert.True(selection.IsEnabled("wikipedia-text"));
        Assert.True(selection.IsEnabled("wikibooks-text"));
        Assert.True(selection.IsEnabled("usgs-earthquakes"));
        Assert.True(selection.IsEnabled("loc-century-of-lawmaking"));
        Assert.True(selection.IsEnabled("gutenberg-catalog"));
        Assert.True(selection.IsEnabled("smithsonian-openaccess-metadata"));
        Assert.False(selection.IsEnabled("openstax-text"));

        var catalog = new SourceRegistry([new StubWikibooksProvider()]);
        Assert.True(catalog.Sources.Single(x => x.Id == "wikibooks-text").Enabled);
        Assert.False(catalog.Sources.Single(x => x.Id == "openstax-text").Enabled);
        Assert.Contains("permission", catalog.Sources.Single(x =>
            x.Id == "openstax-text").PermissionStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenStaxToggle_DoesNotOverrideItsExplicitAiRestriction()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SourceSelection(new Dictionary<string, bool>
            {
                ["openstax-text"] = true
            }));
        Assert.Contains("prior written permission", exception.Message);
        Assert.Contains("openstax-text=false", exception.Message);
    }

    [Fact]
    public void DisabledOrUnknownSources_AreHandledDeterministically()
    {
        var disabled = new SourceSelection(new Dictionary<string, bool>
        {
            ["wikipedia-text"] = false,
            ["wikibooks-text"] = false
        });
        Assert.False(disabled.IsEnabled("wikipedia-text"));
        Assert.False(disabled.IsEnabled("wikibooks-text"));
        Assert.False(disabled.IsEnabled("unknown"));

        var invalid = Assert.Throws<InvalidOperationException>(() =>
            new SourceSelection(new Dictionary<string, bool>
            {
                ["unsupported-web-scraper"] = true
            }));
        Assert.Contains("Unknown educational source", invalid.Message);
    }

    [Theory]
    [InlineData("wikibooks-text", "maybe")]
    [InlineData("openstax-text", "enabled")]
    public void InvalidBooleanSetting_FailsInsteadOfUsingImplicitDefault(
        string provider, string value)
    {
        var settings = new Dictionary<string, string?>
        {
            ["LessonForge:Sources:wikipedia-text"] = "true",
            ["LessonForge:Sources:wikibooks-text"] = "true",
            ["LessonForge:Sources:usgs-earthquakes"] = "true",
            ["LessonForge:Sources:loc-century-of-lawmaking"] = "true",
            ["LessonForge:Sources:gutenberg-catalog"] = "true",
            ["LessonForge:Sources:smithsonian-openaccess-metadata"] = "true",
            ["LessonForge:Sources:openstax-text"] = "false"
        };
        settings["LessonForge:Sources:" + provider] = value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SourceSelection.Load(config));
        Assert.Contains(provider, exception.Message);
    }

    [Fact]
    public void SourceRegistry_DoesNotAcceptOpenStaxAsAnInstalledAiProvider()
    {
        Assert.Throws<ArgumentException>(() =>
            new SourceRegistry([new StubOpenStaxProvider()]));
    }

    private sealed class StubWikibooksProvider : IEducationalSourceProvider
    {
        public string Id => "wikibooks-text";
        public bool SupportsTopic(string topic) => true;
        public Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
            string topic, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SourceDocument>>([]);
    }

    private sealed class StubOpenStaxProvider : IEducationalSourceProvider
    {
        public string Id => "openstax-text";
        public bool SupportsTopic(string topic) => true;
        public Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
            string topic, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SourceDocument>>([]);
    }
}
