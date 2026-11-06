using System.Text.Json;
using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using Microsoft.Extensions.AI;
using Xunit;

namespace LessonForge.Tests;

public sealed class ResearchSummaryRecoveryTests
{
    [Theory]
    [InlineData("Millennialism is a belief, derived from the concept of")]
    [InlineData("Millennialism is a belief, derived from the concept of.")]
    [InlineData("An idea which")]
    [InlineData("An account that")]
    [InlineData("A description with")]
    [InlineData("A description of...")]
    [InlineData("A description of…")]
    [InlineData("A sentence that ends with a comma,")]
    [InlineData("    ")]
    [InlineData(null)]
    public void ObviousIncompleteSummary_IsDetected(string? summary)
    {
        Assert.False(ResearchSummaryRecovery.LooksComplete(summary));
    }

    [Theory]
    [InlineData("Millennialism describes belief in a future age of peace.")]
    [InlineData("Geologists study earthquakes and seismic waves.")]
    [InlineData("The term is used in the U.S.")]
    [InlineData("\"A short summary of the period.\"")]
    [InlineData("Why do historians disagree?")]
    [InlineData("This outcome is supported!")]
    public void CompleteSummary_IsNotRejected(string summary)
    {
        Assert.True(ResearchSummaryRecovery.LooksComplete(summary));
    }

    private static ResearchDraftOutput Draft(string summary) => new()
    {
        TopicSummary = summary,
        GroundedFacts =
        [
            new GroundedFact
            {
                Statement = "A documented historical claim.",
                Citations = [new CitationRef
                {
                    Title = "Verified source",
                    Url = "https://www.loc.gov/item/12345678/"
                }]
            }
        ],
        QueriesUsed = ["historical claim"],
        SourceInventory = [new CitationRef
        {
            Title = "Verified source",
            Url = "https://www.loc.gov/item/12345678/"
        }]
    };

    private sealed class ProgressCollector : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }

    [Fact]
    public async Task CompleteSummary_SkipsModelAndPreservesOriginalReport()
    {
        var original = Draft("A complete and source-grounded account of the historical event.");
        var invoked = false;
        var result = await ResearchSummaryRecovery.EnsureCompleteAsync(
            original,
            _ =>
            {
                invoked = true;
                return Task.FromResult("Should not run.");
            });

        Assert.Same(original, result);
        Assert.False(invoked);
        Assert.Equal("A complete and source-grounded account of the historical event.", result.TopicSummary);
    }

    [Fact]
    public async Task TruncatedSummary_IsReplacedWithoutChangingVerifiedFactsOrCitations()
    {
        var draft = Draft("Millennialism is a belief, derived from the concept of");
        var originalFacts = draft.GroundedFacts;
        var originalFact = draft.GroundedFacts[0];
        var originalCitation = draft.GroundedFacts[0].Citations[0];
        var originalSourceInventory = draft.SourceInventory;
        var progress = new ProgressCollector();
        var calls = 0;

        var result = await ResearchSummaryRecovery.EnsureCompleteAsync(
            draft,
            _ =>
            {
                calls++;
                return Task.FromResult(
                    "Millennialism describes religious expectations of a future age.");
            },
            progress);

        Assert.Same(draft, result);
        Assert.Equal("Millennialism describes religious expectations of a future age.", result.TopicSummary);
        Assert.Equal(1, calls);
        Assert.Same(originalFacts, result.GroundedFacts);
        Assert.Same(originalFact, result.GroundedFacts[0]);
        Assert.Same(originalCitation, result.GroundedFacts[0].Citations[0]);
        Assert.Same(originalSourceInventory, result.SourceInventory);
        Assert.Contains(progress.Messages, m => m.Contains("appears incomplete"));
        Assert.Contains(progress.Messages, m => m.Contains("completeness check passed"));
    }

    [Fact]
    public async Task StillIncompleteRepair_FailsBeforePublishingOriginalFragment()
    {
        var draft = Draft("Millennialism is a belief, derived from the concept of");
        var calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ResearchSummaryRecovery.EnsureCompleteAsync(
                draft,
                _ =>
                {
                    calls++;
                    return Task.FromResult("It is a concept related to");
                });
        });
        Assert.Equal(1, calls);
        Assert.Contains("one targeted regeneration", error.Message);
        Assert.DoesNotContain("Millennialism", error.Message);
        Assert.Equal("Millennialism is a belief, derived from the concept of", draft.TopicSummary);
    }

    [Fact]
    public async Task RepairDoesNotHideModelOrTransportFailures()
    {
        var draft = Draft("An incomplete account of");
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await ResearchSummaryRecovery.EnsureCompleteAsync(
                draft,
                _ => throw new HttpRequestException("Ollama not reachable"));
        });
        Assert.Contains("not reachable", error.Message);
    }

    [Fact]
    public async Task CancellationPreventsRepair()
    {
        var draft = Draft("An incomplete account of");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ResearchSummaryRecovery.EnsureCompleteAsync(
                draft,
                _ =>
                {
                    calls++;
                    return Task.FromResult("It is now complete.");
                },
                ct: cts.Token);
        });
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationDuringRepairDoesNotPublishGeneratedSummary()
    {
        var draft = Draft("An incomplete account of");
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ResearchSummaryRecovery.EnsureCompleteAsync(
                draft,
                _ =>
                {
                    cts.Cancel();
                    return Task.FromResult("A complete sentence about the topic.");
                },
                ct: cts.Token);
        });
        Assert.Equal("An incomplete account of", draft.TopicSummary);
    }

    [Fact]
    public void RepairSchemaOnlyContainsTopicSummary()
    {
        var format = ChatResponseFormat.ForJsonSchema<ResearchSummaryRepairOutput>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var schema = Assert.IsType<ChatResponseFormatJson>(format).Schema;
        Assert.True(schema.HasValue);
        var json = schema.Value.GetRawText();
        Assert.Contains("\"topic_summary\"", json);
        Assert.DoesNotContain("\"grounded_facts\"", json);
        Assert.DoesNotContain("\"source_inventory\"", json);

        var repair = StructuredAgentOutput.Parse<ResearchSummaryRepairOutput>(
            """{"topic_summary":"A concise, complete sentence."}""", "ResearchSummaryRepair");
        Assert.Equal("A concise, complete sentence.", repair.TopicSummary);
    }
}
