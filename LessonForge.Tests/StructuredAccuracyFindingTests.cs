using System.Text.Json;
using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using Xunit;

namespace LessonForge.Tests;

public sealed class StructuredAccuracyFindingTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ValidStructuredFinding_RoundTripsAndPreservesFieldNames()
    {
        var flag = Finding();
        var json = JsonSerializer.Serialize(flag, Web);
        var restored = JsonSerializer.Deserialize<VerificationFlag>(json, Web)!;

        Assert.Contains("\"recommendation\"", json);
        Assert.Contains("\"location\"", json);
        Assert.Equal("assessment_alignment", restored.Category);
        Assert.Equal("Q1", restored.Location);
        Assert.Equal("Teach the concept before asking this question.", restored.Recommendation);
    }

    [Theory]
    [InlineData("High")]
    [InlineData("Medium")]
    [InlineData("Low")]
    [InlineData("Informational")]
    public void AllFourSeverityLevels_AreAccepted(string severity)
    {
        var json = JsonSerializer.Serialize(new FactCheckReviewOutput
        {
            Flags = [Finding(severity: severity)],
            CitationCoverage = 0.5,
            Notes = "Partial review."
        }, Web);
        var result = StructuredAgentOutput.Parse<FactCheckReviewOutput>(json, "FactCheck", "stop");

        Assert.Equal(severity, Assert.Single(result.Flags).Severity);
    }

    [Fact]
    public void OmittedSeverity_DoesNotInheritMediumDefaultFromDto()
    {
        // Even though VerificationFlag.Severity initializes to "Medium" for
        // DTO compatibility, the model must explicitly choose a severity.
        const string incomplete = """
            {
              "flags": [
                {"claim":"Complete finding","issue":"Check evidence","severity":"Low",
                 "category":"missing_citation","location":"Q7",
                 "recommendation":"Add a reference."},
                {"claim":"Omitted severity","issue":"Check another answer",
                 "category":"assessment_alignment","location":"Q8",
                 "recommendation":"Correct the assessment."}
              ],
              "citation_coverage":0.5,
              "notes":"Review is otherwise complete."
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<FactCheckReviewOutput>(
                incomplete, "FactCheck Q7, Q8", "stop"));
        Assert.Contains("flags[1].severity", error.Message);
        Assert.DoesNotContain("flags[0].severity", error.Message);

        var singleError = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<FactCheckReviewOutput>(
                incomplete, "FactCheck Q8", "stop",
                authoritativeSingleTarget: "Q8"));
        Assert.Contains("flags[1].severity", singleError.Message);
    }

    [Fact]
    public void ExplicitInvalidSeverity_IsRejectedRatherThanInventingRubricValue()
    {
        var review = new FactCheckReviewOutput
        {
            Flags = [Finding(severity: "Severe")],
            CitationCoverage = 0.5,
            Notes = "Needs a review."
        };
        var json = JsonSerializer.Serialize(review, Web);

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<FactCheckReviewOutput>(
                json, "FactCheck Q1", "stop", authoritativeSingleTarget: "Q1"));

        Assert.Contains("flags[0].severity", error.Message);
    }

    [Theory]
    [InlineData("unknown", "category")]
    [InlineData("S0", "location")]
    [InlineData("Q999", null)]
    public void InvalidCategoryOrLocation_IsNotSilentlyAccepted(string value, string? expectedField)
    {
        var finding = expectedField == "category"
            ? Finding(category: value)
            : Finding(location: value);
        var review = new FactCheckReviewOutput
        {
            Flags = [finding],
            CitationCoverage = 0.5,
            Notes = "Partial review."
        };

        if (value == "Q999")
        {
            // A syntactically valid reference can still point outside this actual quiz;
            // final assembly, not schema parsing, must reject it.
            Assert.Empty(AccuracyReviewPolicy.InvalidFields(review));
            Assert.Throws<InvalidOperationException>(() =>
                AccuracyReviewPolicy.Prepare(review, Plan(), Quiz()));
            return;
        }

        var json = JsonSerializer.Serialize(review, Web);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<FactCheckReviewOutput>(json, "FactCheck", "stop"));
        Assert.Contains($"flags[0].{expectedField}", exception.Message);
    }

    [Fact]
    public void MissingEducatorAction_IsRejected()
    {
        var review = new FactCheckReviewOutput
        {
            Flags = [Finding(recommendation: "")],
            CitationCoverage = 0.3,
            Notes = "Partial review."
        };
        var json = JsonSerializer.Serialize(review, Web);
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<FactCheckReviewOutput>(json, "FactCheck", "stop"));

        Assert.Contains("flags[0].recommendation", error.Message);
        Assert.DoesNotContain("Partial review.", error.Message);
    }

    [Fact]
    public void DuplicateRootIssue_IsOneFindingAndMostSeriousSeverityWins()
    {
        var review = new FactCheckReviewOutput
        {
            Flags =
            [
                Finding(severity: "Medium", claim: "Visa   running!"),
                Finding(severity: "High", claim: "visa running")
            ],
            CitationCoverage = 0.3,
            Notes = "Two descriptions of one gap."
        };
        var first = AccuracyReviewPolicy.Prepare(review, Plan(), Quiz());
        var second = AccuracyReviewPolicy.Prepare(review, Plan(), Quiz());

        var flag = Assert.Single(first);
        Assert.Equal("High", flag.Severity);
        Assert.Equal("Q1", flag.Location);
        Assert.Equal("assessment_alignment", flag.Category);
        Assert.StartsWith("LF-", flag.Id);
        Assert.Equal(flag.Id, Assert.Single(second).Id);
        Assert.Equal(2, review.Flags.Count); // No mutation of the model response.
    }

    [Fact]
    public void DifferentIssues_AreNotMergedJustBecauseTheyShareWords()
    {
        var review = new FactCheckReviewOutput
        {
            Flags =
            [
                Finding(category: "assessment_alignment", location: "Q1"),
                Finding(category: "missing_citation", location: "Q1"),
                Finding(category: "assessment_alignment", location: "S1"),
                Finding(category: "assessment_alignment", location: "Q1", claim: "visa duration")
            ],
            CitationCoverage = 0.2,
            Notes = "Independent defects."
        };
        var flags = AccuracyReviewPolicy.Prepare(review, Plan(), Quiz());

        Assert.Equal(4, flags.Count);
        Assert.Equal(4, flags.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void OldSavedLessonFlags_CanStillBeDeserialized()
    {
        const string old = """
            {"claim":"Q1","issue":"Needs citation","severity":"Low"}
            """;
        var flag = JsonSerializer.Deserialize<VerificationFlag>(old, Web)!;
        Assert.Equal("Low", flag.Severity);
        Assert.Equal("", flag.Id);
        Assert.Equal("", flag.Location);
        Assert.Equal("", flag.Recommendation);
    }

    private static VerificationFlag Finding(
        string severity = "High",
        string claim = "visa running",
        string category = "assessment_alignment",
        string location = "Q1",
        string recommendation = "Teach the concept before asking this question.") => new()
    {
        Claim = claim,
        Issue = "The lesson does not introduce the tested concept.",
        Severity = severity,
        Category = category,
        Location = location,
        Recommendation = recommendation,
        Evidence = "No corresponding explanation found in the lesson excerpts."
    };

    private static LessonPlanOutput Plan() => new()
    {
        Sections = [new LessonSection { Title = "Overview", Body = "Travel documents are discussed." }]
    };

    private static QuizOutput Quiz() => new()
    {
        Questions = [new QuizQuestion { Stem = "What is visa running?", Options = ["One", "Two"], CorrectIndex = 0 }]
    };
}
