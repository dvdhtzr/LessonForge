using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using LessonForge.Core.Workflow;
using Xunit;

namespace LessonForge.Tests;

public sealed class AnswerUniquenessTests
{
    private static QuizQuestion Question(string stem = "Where was Carmen Carrozza born?") => new()
    {
        Stem = stem,
        Options = ["Solano, Reggio di Calabria", "Calabria", "Boston", "Paris"],
        CorrectIndex = 0,
        Explanation = "The named town is in Calabria, Italy.",
        Citations = [new CitationRef { Title = "Carmen Carrozza",
            Url = "https://en.wikipedia.org/wiki/Carmen_Carrozza" }]
    };

    private static AnswerUniquenessReview Review(
        bool clear = true, params int[] defensible) => new()
    {
        StemClear = clear,
        Notes = "Independent assessment based on supplied excerpts.",
        OptionVerdicts = Enumerable.Range(0, 4).Select(index => new AnswerOptionVerdict
        {
            Index = index,
            Verdict = defensible.Contains(index) ? "defensible" : "not_defensible",
            Reason = "Based on the scope and evidence."
        }).ToList()
    };

    [Fact]
    public void CalabriaAndSpecificTown_BothDefensible_AreRejected()
    {
        Assert.Contains("2 defensible", AnswerUniquenessPolicy.Problem(
            Review(true, 0, 1), Question()));
    }

    [Fact]
    public void UniqueAnswerMatchingKey_IsAccepted()
        => Assert.Null(AnswerUniquenessPolicy.Problem(Review(true, 0), Question()));

    [Fact]
    public void UniqueAnswerDisagreeingWithKey_IsRejected()
        => Assert.Contains("disagrees", AnswerUniquenessPolicy.Problem(
            Review(true, 1), Question()));

    [Fact]
    public void MalformedCarrozzaQuestion_IsRejectedEvenWithOneCandidate()
        => Assert.Contains("malformed", AnswerUniquenessPolicy.Problem(
            Review(false, 0), Question("In what year did Carmen Carro: 1921")));

    [Fact]
    public void UncertainVerdict_IsNotTreatedAsWrongAnswer()
    {
        var review = Review(true, 0);
        review.OptionVerdicts[1].Verdict = "uncertain";
        Assert.Contains("insufficient evidence", AnswerUniquenessPolicy.Problem(
            review, Question()));
    }

    [Fact]
    public void MissingOrDuplicatePerOptionVerdicts_FailClosed()
    {
        var review = Review(true, 0);
        review.OptionVerdicts[3].Index = 2;
        Assert.Throws<InvalidOperationException>(() =>
            AnswerUniquenessPolicy.Problem(review, Question()));

        review = Review(true, 0);
        review.OptionVerdicts.RemoveAt(3);
        Assert.Throws<InvalidOperationException>(() =>
            AnswerUniquenessPolicy.Problem(review, Question()));
    }

    [Theory]
    [InlineData("{\"stem_clear\":true,\"option_verdicts\":[],\"notes\":\"test\"}", "option_verdicts")]
    [InlineData("{\"stem_clear\":true,\"option_verdicts\":[{\"verdict\":\"defensible\",\"reason\":\"yes\"}],\"notes\":\"test\"}", "index")]
    [InlineData("{\"stem_clear\":true,\"option_verdicts\":[{\"index\":0,\"verdict\":\"maybe\",\"reason\":\"yes\"}],\"notes\":\"test\"}", "verdict")]
    [InlineData("{\"option_verdicts\":[{\"index\":0,\"verdict\":\"defensible\",\"reason\":\"yes\"}],\"notes\":\"test\"}", "stem_clear")]
    public void ModelReview_WithMissingOrInvalidFields_IsRejected(
        string json, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<AnswerUniquenessReview>(json, "AnswerUniqueness Q1", "stop"));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task AmbiguousQuestion_IsReplacedOnceAndIndependentlyRechecked()
    {
        var quiz = new QuizOutput { Questions = [Question()] };
        var reviews = 0;
        var replacements = 0;
        var result = await AnswerUniquenessPolicy.VerifyAndRepairAsync(quiz, 4,
            (_, q, _) =>
            {
                reviews++;
                return Task.FromResult(reviews == 1 ? Review(true, 0, 1) : Review(true, 0));
            },
            (_, q, _, _) =>
            {
                replacements++;
                var replacement = Question("Which town was Carmen Carrozza born in?");
                replacement.Options = ["Solano", "Boston", "Paris", "Rome"];
                return Task.FromResult(replacement);
            });

        Assert.Equal(2, reviews);
        Assert.Equal(1, replacements);
        Assert.NotSame(quiz, result);
        Assert.Equal("Where was Carmen Carrozza born?", quiz.Questions[0].Stem);
        Assert.StartsWith("Which town", result.Questions[0].Stem);
    }

    [Fact]
    public async Task PersistentlyAmbiguousQuestion_FailsAfterOneReplacement()
    {
        var reviews = 0;
        var replacements = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AnswerUniquenessPolicy.VerifyAndRepairAsync(
                new QuizOutput { Questions = [Question()] }, 4,
                (_, _, _) =>
                {
                    reviews++;
                    return Task.FromResult(Review(true, 0, 1));
                },
                (_, _, _, _) =>
                {
                    replacements++;
                    return Task.FromResult(Question());
                }));

        Assert.Equal(2, reviews);
        Assert.Equal(1, replacements);
        Assert.Contains("after one replacement", error.Message);
    }

    [Fact]
    public async Task ValidQuestion_NeverTriggersRepair()
    {
        var repairs = 0;
        var result = await AnswerUniquenessPolicy.VerifyAndRepairAsync(
            new QuizOutput { Questions = [Question()] }, 4,
            (_, _, _) => Task.FromResult(Review(true, 0)),
            (_, _, _, _) =>
            {
                repairs++;
                return Task.FromResult(Question());
            });
        Assert.Equal(0, repairs);
        Assert.Single(result.Questions);
    }

    [Fact]
    public async Task ReplacementWithDuplicateOptions_IsRejectedBeforeRereview()
    {
        var reviews = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AnswerUniquenessPolicy.VerifyAndRepairAsync(
                new QuizOutput { Questions = [Question()] }, 4,
                (_, _, _) =>
                {
                    reviews++;
                    return Task.FromResult(Review(true, 0, 1));
                },
                (_, _, _, _) =>
                {
                    var bad = Question();
                    bad.Options = ["Rome", " Rome ", "Paris", "Boston"];
                    return Task.FromResult(bad);
                }));
        Assert.Contains("duplicate choice", error.Message);
        Assert.Equal(1, reviews);
    }

    [Fact]
    public async Task ProgressReportsOnlyHostOwnedQuestionNumbersAndCodes()
    {
        var statuses = new List<string>();
        var attempts = 0;
        await AnswerUniquenessPolicy.VerifyAndRepairAsync(
            new QuizOutput { Questions = [Question()] }, 4,
            (_, _, _) => Task.FromResult(++attempts == 1
                ? Review(true, 0, 1) : Review(true, 0)),
            (_, _, _, _) =>
            {
                var replacement = Question("Which town was Carmen Carrozza born in?");
                replacement.Options = ["Solano", "Boston", "Paris", "Rome"];
                return Task.FromResult(replacement);
            },
            onStatus: (index, status) => statuses.Add($"{index}:{status}"));

        Assert.Equal(new[] { "0:repair_requested", "0:repaired" }, statuses);
    }

    [Fact]
    public void IndependentReviewPrompt_OmitsMarkedKeyAndExplanation()
    {
        var question = Question();
        var lesson = new LessonPlanOutput { Title = "The accordionist",
            Sections = [new LessonSection { Title = "Birth", Body = "Born in Solano in Calabria." }] };
        var prompt = AnswerUniquenessAgent.BuildReviewPrompt(
            lesson, question, "SOURCE: biographical record.", 14);
        Assert.Contains("Solano, Reggio di Calabria", prompt);
        Assert.Contains("Calabria", prompt);
        Assert.DoesNotContain("correct_index", prompt);
        Assert.DoesNotContain("ORIGINAL MARKED INDEX", prompt);
        Assert.DoesNotContain(question.Explanation, prompt);
        Assert.Contains("Classify EVERY option independently", prompt);
    }

    [Fact]
    public void Workflow_ExposesAnswerUniquenessAsSeparateStage()
    {
        var stages = LessonWorkflow.StageOrder;
        Assert.True(Array.IndexOf(stages.ToArray(), "QuizGenerator") <
            Array.IndexOf(stages.ToArray(), "AnswerUniqueness"));
        Assert.True(Array.IndexOf(stages.ToArray(), "AnswerUniqueness") <
            Array.IndexOf(stages.ToArray(), "FactCheck"));
    }
}
