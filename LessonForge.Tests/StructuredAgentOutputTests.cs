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
            StructuredAgentOutput.Parse<ResearchOutput>("  ", "Research", "Length", "TextReasoningContent"));

        Assert.Contains("no final JSON", error.Message);
        Assert.Contains("Length", error.Message);
        Assert.Contains("TextReasoningContent", error.Message);
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
