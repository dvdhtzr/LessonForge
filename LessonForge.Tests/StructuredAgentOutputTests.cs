using System.Text.Json;
using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using Microsoft.Extensions.AI;
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
    public void ResearchSchema_RequiresGroundedFactsAndCitationFields()
    {
        var format = ChatResponseFormat.ForJsonSchema<ResearchOutput>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var schema = Assert.IsType<ChatResponseFormatJson>(format).Schema;
        Assert.True(schema.HasValue);
        var json = schema.Value.GetRawText();

        Assert.Contains("\"topic_summary\"", json);
        Assert.Contains("\"grounded_facts\"", json);
        Assert.Contains("\"statement\"", json);
        Assert.Contains("\"citations\"", json);
        Assert.Contains("\"url\"", json);
    }

    [Fact]
    public void ResearchMissingFacts_DiagnosesAbsentPropertyWithoutLeakingText()
    {
        const string response = """
            {
              "topic_summary":"A private teacher summary",
              "facts":[{"statement":"A private statement"}],
              "source_inventory":[]
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<ResearchOutput>(response, "Research", "stop"));

        Assert.Contains("grounded_facts (property absent)", error.Message);
        Assert.DoesNotContain("A private teacher summary", error.Message);
        Assert.DoesNotContain("A private statement", error.Message);
    }

    [Fact]
    public void ResearchEmptyFacts_DiagnosesEmptyArray()
    {
        const string response = """
            {"topic_summary":"Summary","grounded_facts":[],"queries_used":[],"source_inventory":[]}
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<ResearchOutput>(response, "Research", "stop"));

        Assert.Contains("grounded_facts (empty array)", error.Message);
    }

    [Fact]
    public void CurriculumDesignerSchema_ContainsRequiredWireFieldNames()
    {
        // Unlike ChatResponseFormat.Json, this sends an actual DTO schema to a
        // provider that supports structured response formats.
        var format = ChatResponseFormat.ForJsonSchema<LessonPlanOutput>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var schema = Assert.IsType<ChatResponseFormatJson>(format).Schema;
        Assert.True(schema.HasValue);
        var json = schema.Value.GetRawText();
        Assert.Contains("\"sections\"", json);
        Assert.Contains("\"learning_objectives\"", json);
        Assert.Contains("\"duration_minutes\"", json);
        Assert.Contains("\"citations\"", json);
    }

    [Fact]
    public void PlannerWrongFieldNames_ReportsMissingFields()
    {
        const string json = """
            {
              "title":"The French Revolution",
              "overview":"A useful overview.",
              "objectives":["Explore the causes."],
              "lesson_sections":[{"heading":"Events","text":"A paragraph."}]
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<LessonPlanOutput>(json, "LessonPlanner", "stop"));

        Assert.Contains("learning_objectives", error.Message);
        Assert.Contains("sections", error.Message);
        Assert.DoesNotContain("The French Revolution", error.Message);
    }

    [Fact]
    public void PlannerWithSectionMissingBody_IsRejected()
    {
        const string json = """
            {
              "title":"The French Revolution",
              "overview":"A useful overview.",
              "learning_objectives":["Explain the causes."],
              "sections":[{"title":"The beginning","activities":["Discuss the causes"],"duration_minutes":15}]
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<LessonPlanOutput>(json, "LessonPlanner", "stop"));

        Assert.Contains("sections[0].body", error.Message);
    }

    [Fact]
    public void PlannerCompleteShape_ParsesAndPreservesAllSections()
    {
        const string json = """
            {
              "title":"The French Revolution",
              "grade_band":"Grade 7",
              "overview":"A concise introduction.",
              "learning_objectives":["Identify one cause of the revolution."],
              "sections":[{
                "title":"Background",
                "body":"A short, grounded explanation of historical conditions.",
                "activities":["Discuss one cause"],
                "duration_minutes":15
              }],
              "vocabulary":[{"term":"Estates","definition":"Social divisions."}],
              "materials":["Map"],
              "citations":[{"title":"Encyclopedia","url":"https://example.org/source"}]
            }
            """;

        var plan = StructuredAgentOutput.Parse<LessonPlanOutput>(json, "LessonPlanner", "stop");
        Assert.Equal("The French Revolution", plan.Title);
        Assert.Single(plan.Sections);
        Assert.Equal("Background", plan.Sections[0].Title);
        Assert.Equal("A short, grounded explanation of historical conditions.", plan.Sections[0].Body);
        Assert.Single(plan.Citations);
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
