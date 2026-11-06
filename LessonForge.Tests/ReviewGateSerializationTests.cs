using System.Text.Json;
using LessonForge.Core.Dto;
using Xunit;

namespace LessonForge.Tests;

public sealed class ReviewGateSerializationTests
{
    [Fact]
    public void ResearchReviewFrame_UsesTheFieldsExpectedByTheFrontend()
    {
        var research = new ResearchOutput
        {
            TopicSummary = "The French Revolution began in 1789.",
            GroundedFacts =
            [
                new GroundedFact
                {
                    Statement = "The Estates-General met in 1789.",
                    Citations =
                    [
                        new CitationRef
                        {
                            Title = "Source A",
                            Url = "https://example.org/french-revolution"
                        }
                    ]
                }
            ],
            SourceInventory =
            [
                new CitationRef
                {
                    Title = "Source A",
                    Url = "https://example.org/french-revolution"
                }
            ]
        };

        // Match LessonRunService's SSE event serialization: the payload is boxed
        // inside an anonymous envelope using JsonSerializerDefaults.Web.
        var envelope = new
        {
            requestId = "approval-1",
            stage = "research",
            payload = (object)research
        };
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("The French Revolution began in 1789.",
            payload.GetProperty("topic_summary").GetString());
        var facts = payload.GetProperty("grounded_facts");
        Assert.Equal(1, facts.GetArrayLength());
        Assert.Equal("The Estates-General met in 1789.",
            facts[0].GetProperty("statement").GetString());

        var citations = facts[0].GetProperty("citations");
        Assert.Equal("Source A", citations[0].GetProperty("title").GetString());
        Assert.Equal("https://example.org/french-revolution",
            citations[0].GetProperty("url").GetString());
        Assert.Single(payload.GetProperty("source_inventory").EnumerateArray());
    }

    [Fact]
    public void EditedResearchJson_RoundTripsToTheSameFactAndCitations()
    {
        const string edited = """
            {
              "topic_summary": "Teacher-approved summary.",
              "grounded_facts": [
                {
                  "statement": "Teacher-adjusted factual statement.",
                  "citations": [{"title":"Reference","url":"https://example.org"}]
                }
              ],
              "queries_used": [],
              "source_inventory": []
            }
            """;

        var research = JsonSerializer.Deserialize<ResearchOutput>(
            edited, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(research);
        Assert.Equal("Teacher-adjusted factual statement.", research.GroundedFacts[0].Statement);
        Assert.Equal("https://example.org", research.GroundedFacts[0].Citations[0].Url);
    }
}
