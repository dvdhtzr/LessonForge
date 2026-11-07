using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using Xunit;

namespace LessonForge.Tests;

public sealed class AccuracyReviewBatcherTests
{
    [Fact]
    public void ScopePlan_IncludesEverySectionAndQuizQuestionExactlyOnce()
    {
        var scopes = AccuracyReviewBatcher.Plan(Plan(5), Quiz(3));

        Assert.Equal(5, scopes.Count);
        Assert.Equal(
            new[] { "S1, S2", "S3, S4", "S5", "Q1, Q2", "Q3" },
            scopes.Select(s => s.Label).ToArray());
        Assert.Equal(8, scopes.Sum(s => s.ItemCount));
    }

    [Fact]
    public async Task CompletedScopes_AggregateAllFindingsAndWeightedCoverage()
    {
        var calls = new List<string>();
        var result = await AccuracyReviewBatcher.RunAsync(
            Plan(3), Quiz(2),
            (scope, _) =>
            {
                calls.Add(scope.Label);
                var isSingle = scope.ItemCount == 1;
                return Task.FromResult(Review(
                    scope.Sections.Count > 0 ? $"S{scope.Sections[0] + 1}" : $"Q{scope.Questions[0] + 1}",
                    isSingle ? 0.2 : 0.8));
            });

        Assert.Equal(new[] { "S1, S2", "S3", "Q1, Q2" }, calls);
        Assert.Equal(3, result.Flags.Count);
        Assert.Equal(0.68, result.CitationCoverage, precision: 8);
        Assert.Contains("S3", result.Notes);
        Assert.Contains("AI-estimated", result.Notes);
    }

    [Fact]
    public async Task TruncatedTwoItemScope_IsSplitAndEveryItemIsReviewed()
    {
        var calls = new List<string>();
        var result = await AccuracyReviewBatcher.RunAsync(
            Plan(2), Quiz(1),
            (scope, _) =>
            {
                calls.Add(scope.Label);
                if (scope.Label == "S1, S2")
                    throw new InvalidOperationException(
                        "FactCheck S1, S2 output was truncated after 2339 characters (finish reason: length).");
                return Task.FromResult(Review(scope.Label, 0.5));
            });

        Assert.Equal(new[] { "S1, S2", "S1", "S2", "Q1" }, calls);
        Assert.Equal(3, result.Flags.Count);
        Assert.DoesNotContain(result.Flags, f => f.Claim.Contains("partial"));
    }

    [Fact]
    public async Task AmbiguousTwoItemMissingLocations_ReplaysAsSingleItemScopes()
    {
        var calls = new List<string>();
        var review = await AccuracyReviewBatcher.RunAsync(
            Plan(2), Quiz(1),
            (scope, _) =>
            {
                calls.Add(scope.Label);
                if (scope.ItemCount > 1)
                    throw new InvalidOperationException(
                        "FactCheck S1, S2 returned valid JSON but omitted required review fields. " +
                        "Missing or invalid fields: flags[0].location, flags[1].location. " +
                        "The result was rejected rather than treating an empty payload as successful.");

                return Task.FromResult(Review(scope.Label, 0.5));
            });

        Assert.Equal(new[] { "S1, S2", "S1", "S2", "Q1" }, calls);
        Assert.Equal(new[] { "S1", "S2", "Q1" },
            review.Flags.Select(f => f.Location).ToArray());
        Assert.Equal(0.5, review.CitationCoverage, precision: 8);
    }

    [Fact]
    public async Task FactCheckQ7Q8_MissingSeveritySplitsAndRetainsBothIndependentReviews()
    {
        var calls = new List<string>();
        const string incomplete = """
            {
              "flags": [
                {"claim":"First finding","issue":"Explain missing context","severity":"Low",
                 "category":"instructional_gap","location":"Q7",
                 "recommendation":"Add the missing explanation."},
                {"claim":"Second finding","issue":"Evidence is incomplete","severity":"",
                 "category":"insufficient_evidence","location":"Q8",
                 "recommendation":"Verify the supporting evidence."}
              ],
              "citation_coverage":0.5,
              "notes":"Two-question review."
            }
            """;

        var result = await AccuracyReviewBatcher.RunAsync(
            Plan(1), Quiz(8),
            (scope, _) =>
            {
                calls.Add(scope.Label);
                if (scope.Label == "Q7, Q8")
                    return Task.FromResult(StructuredAgentOutput.Parse<FactCheckReviewOutput>(
                        incomplete, "FactCheck Q7, Q8", "stop"));
                return Task.FromResult(Review(
                    scope.Sections.Count > 0
                        ? $"S{scope.Sections[0] + 1}"
                        : $"Q{scope.Questions[0] + 1}", 0.5));
            });

        Assert.Equal(1, calls.Count(item => item == "Q7, Q8"));
        Assert.Equal(1, calls.Count(item => item == "Q7"));
        Assert.Equal(1, calls.Count(item => item == "Q8"));
        Assert.Equal(2, result.Flags.Count(f => f.Location is "Q7" or "Q8"));
        Assert.Equal(0.5, result.CitationCoverage, precision: 8);
    }

    [Fact]
    public async Task SingleItemMissingSeverityGetsOneFullRegenerationWithoutDefaulting()
    {
        var attempts = 0;
        var result = await AccuracyReviewBatcher.RunAsync(Plan(1), Quiz(7),
            (scope, _) =>
            {
                if (scope.Label == "Q7" && ++attempts == 1)
                    throw new InvalidOperationException(
                        "FactCheck Q7 returned valid JSON but omitted required review fields. " +
                        "Missing or invalid fields: flags[0].severity. " +
                        "The result was rejected rather than treating an empty payload as successful.");

                return Task.FromResult(Review(
                    scope.Sections.Count > 0
                        ? $"S{scope.Sections[0] + 1}"
                        : $"Q{scope.Questions[0] + 1}", 0.5));
            });

        Assert.Equal(2, attempts);
        Assert.Contains(result.Flags, flag =>
            flag.Location == "Q7" && flag.Severity == "Medium");
    }

    [Fact]
    public async Task PersistentMissingSeverityFailsClosedAfterOneRetry()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccuracyReviewBatcher.RunAsync(Plan(1), Quiz(7), (scope, _) =>
            {
                if (scope.Label == "Q7")
                {
                    attempts++;
                    throw new InvalidOperationException(
                        "FactCheck Q7 returned valid JSON but omitted required review fields. " +
                        "Missing or invalid fields: flags[0].severity.");
                }

                return Task.FromResult(Review(
                    scope.Sections.Count > 0
                        ? $"S{scope.Sections[0] + 1}"
                        : $"Q{scope.Questions[0] + 1}", 0.5));
            }));

        Assert.Equal(2, attempts);
        Assert.Contains("flags[0].severity", error.Message);
    }

    [Fact]
    public async Task UnrelatedModelFailureDoesNotTriggerRepeatedCalls()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccuracyReviewBatcher.RunAsync(Plan(1), Quiz(1), (scope, _) =>
            {
                calls++;
                throw new InvalidOperationException("A backend transport failure.");
            }));

        Assert.Equal(1, calls);
        Assert.Contains("backend transport failure", error.Message);
    }

    [Fact]
    public async Task MissingLocationOnSingleItem_StillFailsIfNotHostRestored()
    {
        var calls = new List<string>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccuracyReviewBatcher.RunAsync(
                Plan(1), Quiz(1),
                (scope, _) =>
                {
                    calls.Add(scope.Label);
                    var review = Review(scope.Label, 0.5);
                    review.Flags[0].Location = "";
                    return Task.FromResult(review);
                }));

        Assert.Contains("flags[0].location", error.Message);
        Assert.Equal(new[] { "S1" }, calls);
    }

    [Fact]
    public async Task FactCheckS3_MissingLocationGetsOneBoundedRegeneration()
    {
        var attempts = 0;
        var result = await AccuracyReviewBatcher.RunAsync(
            Plan(3), Quiz(1), (scope, _) =>
            {
                if (scope.Label == "S3" && ++attempts == 1)
                    throw new InvalidOperationException(
                        "FactCheck S3 returned valid JSON but omitted required review fields. " +
                        "Missing or invalid fields: flags[0].location, flags[1].location. " +
                        "The result was rejected rather than treating an empty payload as successful.");
                return Task.FromResult(Review(scope.Label.Split(',')[0].Trim(), 0.5));
            });

        Assert.Equal(2, attempts);
        Assert.Contains(result.Flags, f => f.Location == "S3");
    }

    [Fact]
    public async Task FactCheckS3_PersistentMissingLocationsFailsClosedAfterOneRetry()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccuracyReviewBatcher.RunAsync(Plan(3), Quiz(1), (scope, _) =>
            {
                if (scope.Label == "S3")
                {
                    attempts++;
                    throw new InvalidOperationException(
                        "FactCheck S3 returned valid JSON but omitted required review fields. " +
                        "Missing or invalid fields: flags[0].location, flags[1].location.");
                }
                return Task.FromResult(Review(scope.Label.Split(',')[0].Trim(), 0.5));
            }));

        Assert.Equal(2, attempts);
        Assert.Contains("flags[0].location", error.Message);
    }

    [Fact]
    public async Task TruncatedSingleItem_FailsClosedRatherThanSavingPartialReview()
    {
        var calls = new List<string>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccuracyReviewBatcher.RunAsync(Plan(1), Quiz(1), (scope, _) =>
            {
                calls.Add(scope.Label);
                throw new InvalidOperationException(
                    "FactCheck S1 output was truncated after 500 characters (finish reason: length).");
            }));

        Assert.Contains("output was truncated", error.Message);
        Assert.Equal(new[] { "S1" }, calls);
    }

    [Fact]
    public async Task WrongScopeOrMalformedFinding_IsRejectedBeforeAggregation()
    {
        var wrongScope = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccuracyReviewBatcher.RunAsync(Plan(1), Quiz(1), (scope, _) =>
                Task.FromResult(Review("Q1", 0.5))));
        Assert.Contains("outside S1", wrongScope.Message);

        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccuracyReviewBatcher.RunAsync(Plan(1), Quiz(1), (scope, _) =>
            {
                var review = Review("S1", 0.5);
                review.Flags[0].Recommendation = "";
                return Task.FromResult(review);
            }));
        Assert.Contains("flags[0].recommendation", invalid.Message);
    }

    [Fact]
    public void ScopedPrompt_RemainsBoundedAndNamesOnlyTheTargetForDetailedReview()
    {
        var plan = Plan(6);
        plan.Sections[4].Body = new string('X', 25000);
        var quiz = Quiz(4);
        var scope = AccuracyReviewBatcher.Plan(plan, quiz)[2]; // S5, S6
        var prompt = FactCheckAgent.BuildScopedReviewPrompt(
            plan, quiz, null, "SOURCE EXCERPT", scope, audienceAge: 12);

        Assert.Contains("Review ONLY S5, S6", prompt);
        Assert.Contains("S5:", prompt);
        Assert.Contains("S6:", prompt);
        Assert.Contains("SOURCE EXCERPT", prompt);
        Assert.DoesNotContain("TARGET ITEMS (audit ONLY these identifiers):\nQ1:", prompt);
        Assert.True(prompt.Length < 6500);
    }

    [Fact]
    public void ScopedPrompt_HugeLessonMapIsCappedWithoutHidingTarget()
    {
        var plan = Plan(100);
        var quiz = Quiz(1);
        var scope = AccuracyReviewBatcher.Plan(plan, quiz)[49]; // S99 and S100
        var prompt = FactCheckAgent.BuildScopedReviewPrompt(
            plan, quiz, null, "SOURCE EXCERPT", scope, audienceAge: 12);

        Assert.Contains("Review ONLY S99, S100", prompt);
        Assert.Contains("S100:", prompt);
        Assert.Contains("additional lesson sections omitted", prompt);
        Assert.True(prompt.Length < 6500);
    }

    private static FactCheckReviewOutput Review(string location, double coverage) => new()
    {
        Flags =
        [
            new VerificationFlag
            {
                Claim = $"Claim at {location}",
                Issue = "Further supporting detail is needed.",
                Severity = "Medium",
                Category = "insufficient_evidence",
                Location = location,
                Recommendation = "Inspect the retrieved reference and clarify the claim."
            }
        ],
        CitationCoverage = coverage,
        Notes = "Evidence remains limited."
    };

    private static LessonPlanOutput Plan(int count) => new()
    {
        Title = "A lesson",
        GradeBand = "Grade 7",
        LearningObjectives = ["Explain the topic"],
        Sections = Enumerable.Range(1, count)
            .Select(i => new LessonSection { Title = $"Section {i}", Body = $"Teach topic {i}." })
            .ToList()
    };

    private static QuizOutput Quiz(int count) => new()
    {
        Questions = Enumerable.Range(1, count)
            .Select(i => new QuizQuestion
            {
                Stem = $"Question {i}?",
                Options = ["Yes", "No"],
                CorrectIndex = 0,
                Explanation = "An explanation"
            }).ToList()
    };
}
