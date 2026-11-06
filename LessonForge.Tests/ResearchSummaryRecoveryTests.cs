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

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"topic_summary\":\"\"}")]
    public void MissingSummaryInRepairJson_IsRejectedBeforeUse(string response)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<ResearchSummaryRepairOutput>(
                response, "ResearchSummaryRepair", "stop"));
        Assert.Contains("research summary", error.Message);
        Assert.Contains("topic_summary", error.Message);
        Assert.DoesNotContain(response, error.Message);
    }

    [Fact]
    public async Task ValidJsonAboutWrongPerson_TriggersSubjectIdentityRepair()
    {
        var draft = Draft("The provided sources discuss the acting career of Jesús Carroza.");
        var facts = draft.GroundedFacts;
        var citation = facts[0].Citations[0];
        var calls = 0;
        var messages = new ProgressCollector();

        var repaired = await ResearchSummaryRecovery.EnsureCompleteAsync(
            draft,
            _ =>
            {
                calls++;
                return Task.FromResult(
                    "Carmen Carrozza was an Italian-American concert accordionist.");
            },
            messages,
            expectedNamedSubject: "Carmen Carrozza");

        Assert.Equal(1, calls);
        Assert.Equal("Carmen Carrozza was an Italian-American concert accordionist.",
            repaired.TopicSummary);
        Assert.Same(facts, repaired.GroundedFacts);
        Assert.Same(citation, repaired.GroundedFacts[0].Citations[0]);
        Assert.Contains(messages.Messages, m =>
            m.Contains("does not identify the requested subject", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidJsonStillAboutWrongPerson_FailsClosedAfterSingleRepair()
    {
        var draft = Draft("Jesús Carroza is a Spanish actor.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ResearchSummaryRecovery.EnsureCompleteAsync(
                draft,
                _ => Task.FromResult("Jesús Carroza won the Goya Award."),
                expectedNamedSubject: "Carmen Carrozza");
        });
        Assert.Contains("similarly named person", error.Message);
        Assert.Equal("Jesús Carroza is a Spanish actor.", draft.TopicSummary);
        Assert.DoesNotContain("Jesús", error.Message);
    }

    [Fact]
    public async Task CorrectNamedSubjectSummary_DoesNotCallRepair()
    {
        var draft = Draft("Carmen Carrozza was a concert accordionist.");
        var calls = 0;
        var actual = await ResearchSummaryRecovery.EnsureCompleteAsync(
            draft,
            _ =>
            {
                calls++;
                return Task.FromResult("Not needed.");
            },
            expectedNamedSubject: "Carmen Carrozza");
        Assert.Equal(0, calls);
        Assert.Same(draft, actual);
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
