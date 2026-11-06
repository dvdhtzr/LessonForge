using System.Text.Json;
using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using Microsoft.Extensions.AI;
using Xunit;

namespace LessonForge.Tests;

public sealed class AccuracyEditorReviewTests
{
    [Fact]
    public void ReviewSchema_IsCompactAndDoesNotAskForPlanOrQuiz()
    {
        var format = ChatResponseFormat.ForJsonSchema<FactCheckReviewOutput>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var schema = Assert.IsType<ChatResponseFormatJson>(format).Schema;
        Assert.True(schema.HasValue);
        var json = schema.Value.GetRawText();

        Assert.Contains("\"flags\"", json);
        Assert.Contains("\"citation_coverage\"", json);
        Assert.Contains("\"notes\"", json);
        Assert.DoesNotContain("\"plan\"", json);
        Assert.DoesNotContain("\"quiz\"", json);
        Assert.Contains("\"category\"", json);
        Assert.Contains("\"location\"", json);
        Assert.Contains("\"recommendation\"", json);
    }

    [Fact]
    public void CompactReview_WithNoFlagsAndZeroCoverage_IsValid()
    {
        const string json = """
            {"flags":[],"citation_coverage":0,"notes":"Insufficient evidence for most claims."}
            """;

        var review = StructuredAgentOutput.Parse<FactCheckReviewOutput>(json, "FactCheck", "stop");
        Assert.Empty(review.Flags);
        Assert.Equal(0, review.CitationCoverage);
    }

    [Theory]
    [InlineData("""{"flags":[],"notes":"Some claims checked."}""", "citation_coverage")]
    [InlineData("""{"citation_coverage":1,"notes":"Some claims checked."}""", "flags")]
    [InlineData("""{"flags":[],"citation_coverage":1}""", "notes")]
    [InlineData("""{"flags":[],"citation_coverage":1.5,"notes":"Some claims checked."}""", "citation_coverage")]
    [InlineData("""{"flags":[{"claim":"S1","issue":"No citation","severity":"Extreme"}],"citation_coverage":0.5,"notes":"Some claims checked."}""", "flags[0].severity")]
    public void MissingOrInvalidReviewField_IsRejectedWithFieldPath(string json, string field)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<FactCheckReviewOutput>(json, "FactCheck", "stop"));

        Assert.Contains(field, error.Message);
        Assert.DoesNotContain("Some claims checked", error.Message);
    }

    [Fact]
    public void FinalAssembly_PreservesEducatorApprovedArtifactsWithoutRewriting()
    {
        var plan = new LessonPlanOutput
        {
            Title = "Educator's edited title",
            GradeBand = "Grade 7",
            Overview = "Educator-authored overview",
            LearningObjectives = ["Explain one historical cause"],
            Sections = [new LessonSection { Title = "Edited section", Body = "Approved body", DurationMinutes = 15 }],
            Citations = [new CitationRef { Title = "Approved source", Url = "https://example.org/source" }]
        };
        var quiz = new QuizOutput
        {
            Questions = [new QuizQuestion
            {
                Stem = "Educator-approved lesson question?",
                Options = ["Correct", "Wrong"],
                CorrectIndex = 0,
                Explanation = "The approved explanation",
                Citations = [new CitationRef { Title = "Approved source", Url = "https://example.org/source" }]
            }]
        };
        var review = new FactCheckReviewOutput
        {
            Flags = [new VerificationFlag
            {
                Claim = "Quiz explanation",
                Issue = "Needs further evidence",
                Severity = "Low",
                Category = "missing_citation",
                Location = "Q1",
                Recommendation = "Add a direct supporting reference."
            }],
            CitationCoverage = 0.75,
            Notes = "Several claims have supporting sources."
        };

        var result = FactCheckAgent.AssembleVerifiedLesson(plan, quiz, review);
        Assert.Same(plan, result.Plan);
        Assert.Same(quiz, result.Quiz);
        Assert.Equal("Educator's edited title", result.Plan.Title);
        Assert.Equal("Approved body", result.Plan.Sections[0].Body);
        Assert.Equal(0, result.Quiz.Questions[0].CorrectIndex);
        Assert.Single(result.Flags);
        Assert.StartsWith("LF-", result.Flags[0].Id);
        Assert.NotSame(review.Flags, result.Flags);
        Assert.Equal(0.75, result.CitationCoverage);
        Assert.Equal(review.Notes, result.Notes);
    }

    [Fact]
    public void CompactPrompt_ContainsEvidenceAndIdentifiersButNotEntireLargeLesson()
    {
        var plan = new LessonPlanOutput
        {
            Title = "Long lesson",
            Sections = [new LessonSection { Title = "Section 1", Body = new string('X', 20000) }]
        };
        var quiz = new QuizOutput
        {
            Questions = [new QuizQuestion {
                Stem = "What happened?",
                Options = ["This", "That"],
                CorrectIndex = 0,
                Explanation = "Evidence is relevant."
            }]
        };

        var prompt = FactCheckAgent.BuildReviewPrompt(plan, quiz, null, "SOURCE: https://example.org");
        Assert.Contains("S1:", prompt);
        Assert.Contains("Q1:", prompt);
        Assert.Contains("SOURCE: https://example.org", prompt);
        Assert.Contains("Return only a small JSON object", prompt);
        Assert.True(prompt.Length < 6000);
    }

    [Fact]
    public void FinalAssembly_RejectsEmptyContentOrInvalidReview()
    {
        var plan = new LessonPlanOutput();
        var quiz = new QuizOutput();
        var review = new FactCheckReviewOutput { Notes = "No findings", CitationCoverage = 0 };

        Assert.Throws<InvalidOperationException>(() =>
            FactCheckAgent.AssembleVerifiedLesson(plan, quiz, review));

        plan.Sections = [new LessonSection { Title = "A", Body = "B" }];
        quiz.Questions = [new QuizQuestion { Stem = "Q", Options = ["A", "B"] }];
        review.CitationCoverage = 2;
        Assert.Throws<InvalidOperationException>(() =>
            FactCheckAgent.AssembleVerifiedLesson(plan, quiz, review));
    }
}
