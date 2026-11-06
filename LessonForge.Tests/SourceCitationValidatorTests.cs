using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public sealed class SourceCitationValidatorTests
{
    private static readonly Citation[] Retrieved =
    [
        new Citation
        {
            Title = "USGS observed earthquake us1234",
            Url = "https://earthquake.usgs.gov/earthquakes/eventpage/us1234",
            ProviderId = "usgs-earthquakes",
            LicenseId = "US-PD"
        },
        new Citation
        {
            Title = "Earthquake — Wikipedia",
            Url = "https://en.wikipedia.org/wiki/Earthquake",
            ProviderId = "wikipedia-text",
            LicenseId = "CC-BY-SA-4.0"
        }
    ];

    [Fact]
    public void KnownSourceUrl_IsNormalizedToHostRetrievedTitle()
    {
        var citations = new List<CitationRef>
        {
            new()
            {
                Title = "Invented title",
                Url = "https://earthquake.usgs.gov/earthquakes/eventpage/us1234"
            }
        };
        SourceCitationValidator.RequireRetrieved(citations, Retrieved, "Research");
        Assert.Equal("USGS observed earthquake us1234", citations[0].Title);
    }

    [Fact]
    public void UnsupportedOrInventedCitations_AreRejected()
    {
        var citations = new[]
        {
            new CitationRef
            {
                Title = "Citation", Url = "https://not-indexed.example/earthquake"
            }
        };
        var error = Assert.Throws<InvalidOperationException>(() =>
            SourceCitationValidator.RequireRetrieved(citations, Retrieved, "Research"));
        Assert.Contains("not present in this run's rights-eligible source inventory", error.Message);
        Assert.DoesNotContain("not-indexed.example", error.Message);
    }

    [Fact]
    public void TwoSourcesDoNotProveSupportButBothMustBeKnown()
    {
        var refs = new[]
        {
            new CitationRef { Title = "Wiki", Url = Retrieved[1].Url },
            new CitationRef { Title = "USGS", Url = Retrieved[0].Url }
        };
        SourceCitationValidator.RequireRetrieved(refs, Retrieved, "Lesson plan");
        Assert.Equal("Earthquake — Wikipedia", refs[0].Title);
        Assert.Equal("USGS observed earthquake us1234", refs[1].Title);
    }
}
