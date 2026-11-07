using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using System.Text.Json;
using Xunit;

namespace LessonForge.Tests;

/// <summary>
/// Single-target FactCheck no longer asks a local LLM to emit a section ID.
/// Every item is host-owned while substantive observations still require the
/// same strict policy and explicit model-chosen severity.
/// </summary>
public sealed class SingleItemFactCheckBoundaryTests
{
    private const string ThreeFlagsWithoutLocations = """
        {
          "flags": [
            {
              "claim":"Claim one",
              "issue":"Needs more nuance.",
              "severity":"Medium",
              "category":"misleading_wording",
              "recommendation":"Explain the caveat.",
              "evidence":"The source describes an exception."
            },
            {
              "claim":"Claim two",
              "issue":"No direct citation.",
              "severity":"Low",
              "category":"missing_citation",
              "recommendation":"Cite the relevant source.",
              "evidence":""
            },
            {
              "claim":"Claim three",
              "issue":"Source is not conclusive.",
              "severity":"Informational",
              "category":"insufficient_evidence",
              "recommendation":"Describe the evidence limitation.",
              "evidence":"The source does not settle this question."
            }
          ],
          "citation_coverage":0.65,
          "notes":"The reviewed section has evidence limitations."
        }
        """;

    [Fact]
    public void FactCheckS1_WithThreeMissingLocations_IsValidAndBoundToExactTarget()
    {
        var parsed = StructuredAgentOutput.Parse<SingleItemFactCheckOutput>(
            ThreeFlagsWithoutLocations, "FactCheck S1", "stop",
            authoritativeSingleTarget: "S1");

        var review = SingleItemFactCheckBoundary.AssignLocation(parsed, "S1");
        Assert.Equal(3, review.Flags.Count);
        Assert.All(review.Flags, finding => Assert.Equal("S1", finding.Location));
        Assert.Equal(new[] { "Medium", "Low", "Informational" },
            review.Flags.Select(f => f.Severity).ToArray());
        Assert.Equal(0.65, review.CitationCoverage, precision: 8);
        Assert.Empty(AccuracyReviewPolicy.InvalidFields(
            review, sectionCount: 1, questionCount: 0));
    }

    [Fact]
    public void FactCheckSingleTarget_ExtraNoncanonicalLocationCannotOverrideHostScope()
    {
        var raw = ThreeFlagsWithoutLocations.Replace(
            "\"claim\":\"Claim one\"",
            "\"claim\":\"Claim one\",\"location\":\"Section 1\"");
        var parsed = StructuredAgentOutput.Parse<SingleItemFactCheckOutput>(
            raw, "FactCheck S1", "stop", authoritativeSingleTarget: "S1");
        var review = SingleItemFactCheckBoundary.AssignLocation(parsed, "S1");
        Assert.All(review.Flags, f => Assert.Equal("S1", f.Location));
    }

    [Fact]
    public void ExplicitDifferentSection_IsRejectedRatherThanRelabeled()
    {
        var raw = ThreeFlagsWithoutLocations.Replace(
            "\"claim\":\"Claim two\"",
            "\"claim\":\"Claim two\",\"location\":\"Q7\"");
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<SingleItemFactCheckOutput>(
                raw, "FactCheck S1", "stop", authoritativeSingleTarget: "S1"));
        Assert.Contains("flags[1].location", error.Message);
        Assert.Contains("outside S1", error.Message);
    }

    [Fact]
    public void SingleItemMissingSeverity_IsNotInvented()
    {
        var raw = ThreeFlagsWithoutLocations.Replace(
            "\"severity\":\"Low\",", "");
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<SingleItemFactCheckOutput>(
                raw, "FactCheck S1", "stop", authoritativeSingleTarget: "S1"));
        Assert.Contains("flags[1].severity", error.Message);
    }

    [Fact]
    public void SingleItemMissingRecommendation_IsRejected()
    {
        var raw = ThreeFlagsWithoutLocations.Replace(
            "\"recommendation\":\"Cite the relevant source.\",", "");
        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<SingleItemFactCheckOutput>(
                raw, "FactCheck S1", "stop", authoritativeSingleTarget: "S1"));
        Assert.Contains("flags[1].recommendation", error.Message);
    }

    [Theory]
    [InlineData("S0")]
    [InlineData("S1, S2")]
    [InlineData("GENERAL")]
    public void HostCannotAssignAmbiguousOrInvalidTarget(string target)
    {
        var model = new SingleItemFactCheckOutput
        {
            Flags = [],
            CitationCoverage = 1,
            Notes = "No major issues were found."
        };
        Assert.Throws<ArgumentException>(() =>
            SingleItemFactCheckBoundary.AssignLocation(model, target));
    }

    [Fact]
    public void NoFindings_IsValidOnlyWithCompleteReviewMetadata()
    {
        var parsed = StructuredAgentOutput.Parse<SingleItemFactCheckOutput>(
            """
            {"flags":[],"citation_coverage":1.0,"notes":"No issues found."}
            """, "FactCheck Q8", "stop", authoritativeSingleTarget: "Q8");
        var review = SingleItemFactCheckBoundary.AssignLocation(parsed, "Q8");
        Assert.Empty(review.Flags);
        Assert.Equal(1, review.CitationCoverage);
        Assert.Equal("No issues found.", review.Notes);

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<SingleItemFactCheckOutput>(
                """{"flags":[],"notes":"Incomplete."}""", "FactCheck Q8", "stop",
                authoritativeSingleTarget: "Q8"));
        Assert.Contains("citation_coverage", error.Message);
    }

    [Fact]
    public void SingleTargetPrompt_RequestsNoModelAuthoredLocation()
    {
        var plan = new LessonPlanOutput
        {
            Sections = [new LessonSection { Title = "History", Body = "A historical event." }]
        };
        var quiz = new QuizOutput
        {
            Questions = [new QuizQuestion { Stem = "Question", Options = ["A", "B"] }]
        };
        var scope = new AccuracyReviewBatcher.Scope([0], []);
        var prompt = FactCheckAgent.BuildScopedReviewPrompt(
            plan, quiz, null, "Evidence excerpt", scope, 12);
        Assert.Contains("Do not include location or ID", prompt);
        Assert.DoesNotContain("For EVERY flag, include location", prompt);
    }
}
