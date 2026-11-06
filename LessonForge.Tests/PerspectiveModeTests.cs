using System.Text.Json;
using LessonForge.Core.Agents;
using LessonForge.Core.Configuration;
using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
using LessonForge.Core.Workflow;
using Microsoft.Extensions.AI;
using Xunit;

namespace LessonForge.Tests;

public sealed class PerspectiveModeTests
{
    private static readonly CitationRef SourceA = new()
    {
        Title = "Historical source A", Url = "https://example.org/history-a"
    };
    private static readonly CitationRef SourceB = new()
    {
        Title = "Historical source B", Url = "https://example.org/history-b"
    };

    private static PerspectiveAnalysisOutput SupportedAnalysis() => new()
    {
        Status = PerspectiveAnalysisValidator.Supported,
        Perspectives =
        [
            new PerspectiveViewpoint
            {
                Name = "Interpretation A",
                Interpretation = "An interpretation grounded in contemporary accounts.",
                EvidenceBasis = "Contemporary accounts recorded this interpretation.",
                Limitations = "The witnesses did not represent all participants.",
                Citations = [SourceA]
            },
            new PerspectiveViewpoint
            {
                Name = "Interpretation B",
                Interpretation = "A competing account places different weight on events.",
                EvidenceBasis = "Other period accounts emphasize different consequences.",
                Limitations = "Evidence is not conclusive.",
                Citations = [SourceB]
            }
        ],
        AreasOfAgreement = ["Both descriptions concern the same historical period."],
        EvidenceLimitations = "The documentary evidence is incomplete and uneven.",
        DiscussionQuestions = ["Which cited source supports each interpretation?"]
    };

    [Fact]
    public void Request_DefaultsToExistingFourAgentMode()
    {
        var request = JsonSerializer.Deserialize<LessonRequest>(
            """{"topic":"Photosynthesis","audience_age":12}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.False(request.ExplorePerspectives);
        Assert.Null(new ResearchOutput().Perspectives);
        Assert.Null(new VerifiedLessonOutput().Perspectives);
    }

    [Fact]
    public void PerspectiveJsonSchema_ContainsExplicitEvidenceFields()
    {
        var format = ChatResponseFormat.ForJsonSchema<PerspectiveAnalysisOutput>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var schema = Assert.IsType<ChatResponseFormatJson>(format).Schema;
        Assert.True(schema.HasValue);
        var json = schema.Value.GetRawText();

        foreach (var name in new[]
            { "status", "perspectives", "interpretation", "evidence_basis", "limitations",
              "citations", "areas_of_agreement", "evidence_limitations", "discussion_questions" })
            Assert.Contains($"\"{name}\"", json);
    }

    [Fact]
    public void CredibleDisagreement_RequiresRetrievedSources()
    {
        var review = SupportedAnalysis();
        PerspectiveAnalysisValidator.Validate(review, [SourceA, SourceB]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            PerspectiveAnalysisValidator.Validate(review, [SourceA]));
        Assert.Contains("outside the retrieved evidence", error.Message);
    }

    [Fact]
    public void NoMeaningfulDebate_IsValidWithoutInventedOpposition()
    {
        var review = new PerspectiveAnalysisOutput
        {
            Status = PerspectiveAnalysisValidator.NoDisagreement,
            Perspectives = [],
            EvidenceLimitations = "Available sources do not establish another substantiated interpretation.",
            DiscussionQuestions = ["Which primary source supports the established account?"]
        };

        PerspectiveAnalysisValidator.Validate(review, [SourceA]);
        var prompt = PerspectivePrompt.ForLesson(review);
        Assert.Contains("Do not invent a debate", prompt);
        Assert.DoesNotContain("Interpretation A", prompt);
    }

    [Fact]
    public void InconclusiveEvidence_CannotBeRepackagedAsTwoEqualSides()
    {
        var review = SupportedAnalysis();
        review.Status = PerspectiveAnalysisValidator.Insufficient;

        var error = Assert.Throws<InvalidOperationException>(() =>
            PerspectiveAnalysisValidator.Validate(review, [SourceA, SourceB]));
        Assert.Contains("must not introduce competing viewpoints", error.Message);
    }

    [Fact]
    public void MissingEvidenceOrDuplicatePerspectives_IsRejected()
    {
        var analysis = SupportedAnalysis();
        analysis.Perspectives[1].Name = analysis.Perspectives[0].Name;
        var duplicate = Assert.Throws<InvalidOperationException>(() =>
            PerspectiveAnalysisValidator.Validate(analysis, [SourceA, SourceB]));
        Assert.Contains("duplicate perspectives", duplicate.Message);

        analysis = SupportedAnalysis();
        analysis.EvidenceLimitations = "";
        var limitations = Assert.Throws<InvalidOperationException>(() =>
            PerspectiveAnalysisValidator.Validate(analysis, [SourceA, SourceB]));
        Assert.Contains("evidentiary limitations", limitations.Message);
    }

    [Fact]
    public void ApprovedInterpretations_AreIncludedInDownstreamContext()
    {
        var context = PerspectivePrompt.ForLesson(SupportedAnalysis());
        Assert.Contains("Interpretation A", context);
        Assert.Contains("Interpretation B", context);
        Assert.Contains("https://example.org/history-a", context);
        Assert.Contains("not necessarily equally supported", context);
        Assert.Equal("", PerspectivePrompt.ForLesson(null));
    }

    [Fact]
    public void VerifiedLesson_PreservesApprovedPerspectivesAndArtifacts()
    {
        var plan = new LessonPlanOutput
        {
            Title = "Educator-approved lesson",
            Sections = [new LessonSection { Title = "Understanding history", Body = "Approved material." }]
        };
        var quiz = new QuizOutput
        {
            Questions = [new QuizQuestion
            {
                Stem = "What is the relevant evidence?",
                Options = ["Source A", "Source B"],
                Explanation = "The evidence is documented.",
                Citations = [SourceA]
            }]
        };
        var review = new FactCheckReviewOutput
        {
            Flags = [],
            CitationCoverage = 0.6,
            Notes = "Some evidence remains contested."
        };
        var perspectives = SupportedAnalysis();

        var result = FactCheckAgent.AssembleVerifiedLesson(plan, quiz, review, perspectives);
        Assert.Same(plan, result.Plan);
        Assert.Same(quiz, result.Quiz);
        Assert.Same(perspectives, result.Perspectives);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Workflow_BuildsWithAndWithoutOptionalAgent(bool interactive, bool explorePerspectives)
    {
        var clients = new ClientFactory(new LlmOptions());
        using var http = new HttpClient();
        using var embeddings = new FakeEmbeddingGenerator();
        var retrieval = new RetrievalService(
            new WikipediaClient(http), new SourceFetcher(http), embeddings);

        var (workflow, rag) = LessonWorkflow.Build(
            clients, retrieval, interactive, explorePerspectives: explorePerspectives);
        Assert.NotNull(workflow);
        Assert.NotNull(rag);
        Assert.Contains("PerspectiveAnalysis", LessonWorkflow.StageOrder);
    }
}
