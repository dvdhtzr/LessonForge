using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using Xunit;

namespace LessonForge.Tests;

public sealed class StructuredAgentOutputTests
{
    [Fact]
    public void EmptyAnswer_ReportsFinishReasonAndContentTypes()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<ResearchOutput>("  ", "Research", "Stop", "TextReasoningContent"));

        Assert.Contains("no final JSON", error.Message);
        Assert.Contains("Stop", error.Message);
        Assert.Contains("TextReasoningContent", error.Message);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("Length")]
    public void TokenLimit_ReportsTruncationEvenWhenPrefixLooksLikeJson(string finishReason)
    {
        // A provider may stop on a length limit at any point, including a JSON boundary.
        const string apparentlyComplete = """
            {"topic_summary":"short","grounded_facts":[{"statement":"fact","citations":[{"title":"source","url":"https://example.org"}]}]}
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<ResearchOutput>(apparentlyComplete, "Research", finishReason));

        Assert.Contains("output was truncated", error.Message);
        Assert.Contains("MaxOutputTokens", error.Message);
        Assert.DoesNotContain("example.org", error.Message);
    }

    [Fact]
    public void TokenLimit_ReportsIncompleteJsonAsTruncationInsteadOfParserFailure()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<ResearchOutput>(
                """{"topic_summary":"Summary","grounded_facts":[{"statement":"incomplete","citations":[""",
                "Research", "length"));

        Assert.Contains("output was truncated", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void SingularCitationField_IsRejected()
    {
        const string json = """
            {
              "topic_summary": "Short summary.",
              "grounded_facts": [{
                "statement": "An unsupported claim.",
                "citation": {"title": "Source", "url": "https://example.org"}
              }]
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<ResearchOutput>(json, "Research", "stop"));

        Assert.Contains("citation arrays", error.Message);
    }

    [Fact]
    public void InvalidJson_IsRejectedWithoutEchoingResponse()
    {
        const string response = "This is not JSON and should not appear in logs";
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<ResearchOutput>(response, "Research", "Stop"));

        Assert.Contains("malformed or incompatible JSON", error.Message);
        Assert.DoesNotContain(response, error.Message);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void EmptyResearchDto_IsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<ResearchOutput>("{}", "Research"));

        Assert.Contains("omitted required lesson content", error.Message);
    }

    [Fact]
    public void CitedResearchJson_UsesSnakeCaseContracts()
    {
        const string json = """
            {
              "topic_summary": "A short summary.",
              "grounded_facts": [{
                "statement": "A grounded fact.",
                "citations": [{"title": "Reference", "url": "https://example.org/source"}]
              }],
              "queries_used": ["topic"],
              "source_inventory": [{"title": "Reference", "url": "https://example.org/source"}]
            }
            """;

        var result = StructuredAgentOutput.Parse<ResearchOutput>(json, "Research");

        Assert.Equal("A short summary.", result.TopicSummary);
        Assert.Single(result.GroundedFacts);
        Assert.Equal("https://example.org/source", result.GroundedFacts[0].Citations[0].Url);
    }

    [Fact]
    public void EmptyLessonPlanAndQuiz_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<LessonPlanOutput>("{}", "LessonPlanner"));
        Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<QuizOutput>("{}", "QuizGenerator"));
        Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<VerifiedLessonOutput>("{}", "FactCheck"));
    }
}
