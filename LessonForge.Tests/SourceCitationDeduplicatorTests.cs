using LessonForge.Api.Persistence;
using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LessonForge.Tests;

public sealed class SourceCitationDeduplicatorTests
{
    private const string Stanford =
        "https://en.wikipedia.org/wiki/Stanford_prison_experiment";

    private static CitationRef Cite(string url, string title = "Stanford prison experiment")
        => new() { Title = title, Url = url };

    [Fact]
    public void DuplicateStanfordCitation_AppearsOnceAndKeepsFirstAuthoritativeTitle()
    {
        var first = Cite(Stanford, "Stanford prison experiment");
        var references = new List<CitationRef>
        {
            first,
            Cite(Stanford, "Different model-generated title"),
            Cite("https://EN.WIKIPEDIA.ORG/wiki/Stanford_prison_experiment#Background"),
            Cite("https://en.wikipedia.org/wiki/Stanford_prison_experiment?oldid=123"),
            Cite("https://en.wikipedia.org/wiki/Stanford_Prison_Experiment"),
            Cite("https://en.wikipedia.org/wiki/Some_other_study")
        };

        var distinct = SourceCitationDeduplicator.Unique(references);

        Assert.Equal(4, distinct.Count);
        Assert.Same(first, distinct[0]);
        Assert.Equal("Stanford prison experiment", distinct[0].Title);
        Assert.Same(references[3], distinct[1]); // Query selects a different revision.
        Assert.Same(references[4], distinct[2]); // Case-sensitive URL path.
        Assert.Same(references[5], distinct[3]);
        Assert.Equal(6, references.Count); // No change to caller-owned input.
    }

    [Fact]
    public void SameTitleOnDifferentWebPages_PreservesBothSources()
    {
        var references = new[]
        {
            Cite(Stanford),
            Cite("https://en.wikibooks.org/wiki/Psychology/Research_ethics")
        };
        Assert.Equal(2, SourceCitationDeduplicator.Unique(references).Count);
    }

    [Fact]
    public void MissingUrlIsNotHiddenByNormalization()
    {
        var references = new List<CitationRef> { Cite(""), Cite(""), Cite(Stanford) };
        SourceCitationDeduplicator.NormalizeInPlace(references);

        Assert.Equal(3, references.Count); // Existing validation must still reject blanks.
    }

    [Fact]
    public void NormalizationAppliesPerCitationList_NotAcrossDistinctFacts()
    {
        var research = new ResearchOutput
        {
            SourceInventory = [Cite(Stanford), Cite(Stanford)],
            GroundedFacts =
            [
                new GroundedFact
                {
                    Statement = "Fact A",
                    Citations = [Cite(Stanford), Cite(Stanford)]
                },
                new GroundedFact
                {
                    Statement = "Fact B",
                    Citations = [Cite(Stanford), Cite(Stanford)]
                }
            ],
            Perspectives = new PerspectiveAnalysisOutput
            {
                Perspectives =
                [
                    new PerspectiveViewpoint
                    {
                        Name = "A",
                        Citations = [Cite(Stanford), Cite(Stanford)]
                    }
                ]
            }
        };

        SourceCitationDeduplicator.Normalize(research);

        Assert.Single(research.SourceInventory);
        Assert.All(research.GroundedFacts, f => Assert.Single(f.Citations));
        Assert.Single(research.Perspectives.Perspectives[0].Citations);
    }

    [Fact]
    public void VerifiedLessonNormalization_CleansPlanQuizPerspectivesAndProvenance()
    {
        var verified = new VerifiedLessonOutput
        {
            Plan = new LessonPlanOutput
            {
                Title = "Stanford prison experiment",
                Citations = [Cite(Stanford), Cite(Stanford)]
            },
            Quiz = new QuizOutput
            {
                Questions =
                [
                    new QuizQuestion
                    {
                        Stem = "A question?",
                        Citations = [Cite(Stanford), Cite(Stanford)]
                    }
                ]
            },
            Perspectives = new PerspectiveAnalysisOutput
            {
                Perspectives =
                [
                    new PerspectiveViewpoint
                    {
                        Name = "A",
                        Citations = [Cite(Stanford), Cite(Stanford)]
                    }
                ]
            },
            SourceRecords =
            [
                new SourceEvidenceRecord
                {
                    Title = "Stanford prison experiment",
                    Url = Stanford,
                    ProviderId = "wikipedia-text",
                    DocumentSha256 = "hash1"
                },
                new SourceEvidenceRecord
                {
                    Title = "Repeated Stanford prison experiment",
                    Url = Stanford + "#History",
                    ProviderId = "wikipedia-text",
                    DocumentSha256 = "hash1"
                }
            ]
        };

        SourceCitationDeduplicator.Normalize(verified);
        SourceCitationDeduplicator.Normalize(verified); // Idempotence.

        Assert.Single(verified.Plan.Citations);
        Assert.Single(verified.Quiz.Questions[0].Citations);
        Assert.Single(verified.Perspectives.Perspectives[0].Citations);
        var record = Assert.Single(verified.SourceRecords);
        Assert.Equal("hash1", record.DocumentSha256);
        Assert.Equal(Stanford, record.Url);
    }

    [Fact]
    public async Task SavedHistoricalLesson_DeduplicatesOnReadWithoutRewritingDatabase()
    {
        using var sqlite = new SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        var options = new DbContextOptionsBuilder<LessonDbContext>()
            .UseSqlite(sqlite).Options;
        await using var database = new LessonDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var store = new LessonStore(database);

        var lesson = new VerifiedLessonOutput
        {
            Plan = new LessonPlanOutput
            {
                Title = "Stanford prison experiment",
                Citations = [Cite(Stanford), Cite(Stanford)]
            },
            Quiz = new QuizOutput
            {
                Questions = [new QuizQuestion
                {
                    Stem = "What occurred?",
                    Citations = [Cite(Stanford), Cite(Stanford)]
                }]
            },
            SourceRecords =
            [
                new SourceEvidenceRecord { Title = "Stanford", Url = Stanford },
                new SourceEvidenceRecord { Title = "Stanford copy", Url = Stanford }
            ]
        };

        var id = await store.SaveAsync(
            new LessonRequest { Topic = "Stanford prison experiment" }, lesson);

        var record = await database.Lessons.SingleAsync();
        Assert.Contains("\"Stanford copy\"", record.ResultJson);

        var saved = await store.GetAsync(id);
        Assert.NotNull(saved);
        Assert.Equal("Stanford prison experiment", saved.Result.Plan.Title);
        Assert.Single(saved.Result.Plan.Citations);
        Assert.Single(saved.Result.Quiz.Questions[0].Citations);
        Assert.Single(saved.Result.SourceRecords);

        // This is a read-path normalization; historic saved bytes remain intact.
        Assert.Contains("\"Stanford copy\"", record.ResultJson);
    }
}
