using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using Xunit;

namespace LessonForge.Tests;

public sealed class PerspectiveAnalysisRecoveryTests
{
    private static readonly CitationRef[] KnownSources =
    [
        new() { Title = "Document A", Url = "https://www.loc.gov/item/12345678/" },
        new() { Title = "Document B", Url = "https://en.wikipedia.org/wiki/United_States_Constitution" }
    ];

    private static PerspectiveViewpoint View(string name, string url) => new()
    {
        Name = name,
        Interpretation = $"An evidence-based interpretation described by {name}.",
        EvidenceBasis = "Specific evidence in the retrieved excerpt.",
        Limitations = "The retrieved passage does not cover the complete historical record.",
        Citations = [new CitationRef { Title = "A retrieved source", Url = url }]
    };

    private static PerspectiveAnalysisOutput Complete() => new()
    {
        Status = PerspectiveAnalysisValidator.Supported,
        Perspectives =
        [
            View("Interpretation A", KnownSources[0].Url),
            View("Interpretation B", KnownSources[1].Url)
        ],
        AreasOfAgreement = ["Both interpretations discuss the same primary issue."],
        EvidenceLimitations = "Source coverage is incomplete.",
        DiscussionQuestions = ["How do the cited excerpts support different interpretations?"]
    };

    [Fact]
    public async Task ValidFirstAnswerReturnsUnmodifiedWithoutRetry()
    {
        var calls = 0;
        var valid = Complete();
        var result = await PerspectiveAnalysisRecovery.RunAsync(
            (attempt, _) =>
            {
                calls++;
                Assert.Equal(1, attempt);
                return Task.FromResult(valid);
            },
            KnownSources);

        Assert.Same(valid, result);
        Assert.Equal(1, calls);
        Assert.Equal(2, result.Perspectives.Count);
    }

    [Fact]
    public async Task MissingSecondViewEvidenceRetriesThenAcceptsCompleteComparison()
    {
        var attempts = new List<int>();
        var incomplete = Complete();
        incomplete.Perspectives[1].EvidenceBasis = "";
        var valid = Complete();

        var result = await PerspectiveAnalysisRecovery.RunAsync(
            (attempt, _) =>
            {
                attempts.Add(attempt);
                return Task.FromResult(attempt == 1 ? incomplete : valid);
            },
            KnownSources);

        Assert.Equal(new[] { 1, 2 }, attempts);
        Assert.Same(valid, result);
        PerspectiveAnalysisValidator.Validate(result, KnownSources);
    }

    [Fact]
    public async Task RepeatedIncompleteViewpointsBecomeExplicitUnverifiedAnalysis()
    {
        var invalid = Complete();
        invalid.Perspectives[1].Limitations = " ";
        var calls = 0;

        var result = await PerspectiveAnalysisRecovery.RunAsync(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(invalid);
            },
            KnownSources);

        Assert.Equal(2, calls);
        Assert.Equal(PerspectiveAnalysisValidator.Insufficient, result.Status);
        Assert.Empty(result.Perspectives);
        Assert.Empty(result.AreasOfAgreement);
        Assert.Contains("twice returned a comparison", result.EvidenceLimitations);
        Assert.Contains("educator review", result.EvidenceLimitations);
        PerspectiveAnalysisValidator.Validate(result, KnownSources);
    }

    [Fact]
    public async Task UnknownCitationIsNeverAcceptedAndCannotEnterFallback()
    {
        var forged = Complete();
        forged.Perspectives[1].Citations =
            [new CitationRef { Title = "Invented", Url = "https://forged.example/source" }];

        var result = await PerspectiveAnalysisRecovery.RunAsync(
            (_, _) => Task.FromResult(forged),
            KnownSources);

        Assert.Equal(PerspectiveAnalysisValidator.Insufficient, result.Status);
        Assert.Empty(result.Perspectives);
        Assert.DoesNotContain("forged.example", result.EvidenceLimitations);
    }

    [Fact]
    public async Task OnePerspectiveCannotBePresentedAsSupportedDisagreement()
    {
        var invalid = Complete();
        invalid.Perspectives.RemoveAt(1);

        var result = await PerspectiveAnalysisRecovery.RunAsync(
            (_, _) => Task.FromResult(invalid),
            KnownSources);

        Assert.Empty(result.Perspectives);
        Assert.Equal(PerspectiveAnalysisValidator.Insufficient, result.Status);
    }

    [Fact]
    public async Task LegitimateNoDisagreementIsAValidFirstPass()
    {
        var valid = new PerspectiveAnalysisOutput
        {
            Status = PerspectiveAnalysisValidator.NoDisagreement,
            Perspectives = [],
            AreasOfAgreement = [],
            EvidenceLimitations = "There is no established disagreement in the retrieved sources.",
            DiscussionQuestions = []
        };
        var calls = 0;
        var result = await PerspectiveAnalysisRecovery.RunAsync(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(valid);
            },
            KnownSources);
        Assert.Same(valid, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TransportOrGenerationErrorsAreNotDisguisedAsInconclusive()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await PerspectiveAnalysisRecovery.RunAsync(
                (_, _) =>
                {
                    calls++;
                    throw new HttpRequestException("Ollama unreachable");
                },
                KnownSources);
        });
        Assert.Equal(1, calls);
        Assert.Contains("unreachable", error.Message);
    }

    [Fact]
    public async Task CancellationPreventsRetryAndPropagates()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var invalid = Complete();
        invalid.Perspectives[1].EvidenceBasis = "";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await PerspectiveAnalysisRecovery.RunAsync(
                (_, _) =>
                {
                    calls++;
                    cts.Cancel();
                    return Task.FromResult(invalid);
                },
                KnownSources, cts.Token);
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void OutputInstructionsRequireAllFiveEvidenceFieldsAndExplicitInconclusiveOption()
    {
        var prompt = PerspectiveAnalysisRecovery.OutputDiscipline;
        foreach (var field in new[]
        {
            "name", "interpretation", "evidence_basis", "limitations", "citations"
        })
            Assert.Contains(field, prompt);
        Assert.Contains("insufficient_evidence", prompt);
        Assert.Contains("perspectives=[]", prompt);
    }
}
