using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using Xunit;

namespace LessonForge.Tests;

public sealed class QuizAnswerOptionPolicyTests
{
    private static QuizOutput Quiz(params string[] options) => new()
    {
        Questions =
        [
            new QuizQuestion
            {
                Stem = "Where was the musician born?",
                Options = options.ToList(),
                CorrectIndex = 0,
                Explanation = "The answer appears in the lesson.",
                Citations = [new CitationRef { Title = "Source", Url = "https://example.org/source" }]
            }
        ]
    };

    [Fact]
    public void DistinctAnswerOptions_AreAccepted()
        => Assert.Empty(QuizAnswerOptionPolicy.InvalidFields(
            Quiz("Italy", "France", "Germany", "Spain")));

    [Fact]
    public void DuplicateAnswersIgnoringCaseAndWhitespace_AreRejected()
    {
        var quiz = Quiz("Calabria", "France", "  caLAbriA  ", "Spain");
        var errors = QuizAnswerOptionPolicy.InvalidFields(quiz);
        Assert.Contains("questions[0].options[2] (duplicate choice)", errors);
        Assert.Throws<InvalidOperationException>(() => QuizAnswerOptionPolicy.RequireValid(quiz));
    }

    [Fact]
    public void InvalidCorrectIndex_IsRejectedByBothQuizBoundaries()
    {
        var quiz = Quiz("Italy", "France", "Germany", "Spain");
        quiz.Questions[0].CorrectIndex = 4;
        Assert.Contains("questions[0].correct_index", QuizAnswerOptionPolicy.InvalidFields(quiz));
        Assert.Throws<InvalidOperationException>(() =>
            QuizGeneratorAgent.ValidateCounts(quiz, new QuizConfig { QuestionCount = 1, OptionCount = 4 }));
    }

    [Fact]
    public void ValidCountsWithDuplicateOptions_AreRejected()
    {
        var quiz = Quiz("Yes", "  yes", "No", "Maybe");
        var error = Assert.Throws<InvalidOperationException>(() =>
            QuizGeneratorAgent.ValidateCounts(quiz, new QuizConfig { QuestionCount = 1, OptionCount = 4 }));
        Assert.Contains("duplicate choice", error.Message);
    }

    [Fact]
    public void StructuredModelOutput_RejectsDuplicateOptionsWithoutLeakingTheAnswerText()
    {
        const string json = """
            {"questions":[{"stem":"Which answer?","options":["Calabria","Rome"," calabria ","Italy"],
              "correct_index":0,"explanation":"Based on the lesson.",
              "citations":[{"title":"Source","url":"https://example.org/source"}]}]}
            """;
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<QuizOutput>(json, "QuizGenerator", "stop"));
        Assert.Contains("questions[0].options[2]", error.Message);
        Assert.DoesNotContain("Calabria", error.Message);
    }

    [Fact]
    public void NonIdenticalButPotentiallyOverlappingLocations_AreLeftForSemanticReview()
    {
        // An exact-match rule cannot prove whether a specific birthplace and
        // its containing region are both defensible answers to a question.
        var quiz = Quiz("Solano, Reggio di Calabria", "Calabria", "Boston", "Paris");
        Assert.Empty(QuizAnswerOptionPolicy.InvalidFields(quiz));
    }
}
