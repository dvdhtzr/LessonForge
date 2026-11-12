using System.Text.Json;
using LessonForge.Api.Runs;
using LessonForge.Core.Agents;
using LessonForge.Core.Dto;
using Xunit;

namespace LessonForge.Tests;

public sealed class AnswerUniquenessTraceTests
{
    private const string RunId = "ab12cd34ef56";

    private static QuizQuestion Question() => new()
    {
        Stem = "Where was Carmen Carrozza born?",
        Options = ["Solano", "Calabria", "Boston", "Paris"],
        CorrectIndex = 0,
        Explanation = "The town is inside the Calabria region.",
        Citations = [new CitationRef { Title = "Biography", Url = "https://example.org/bio" }]
    };

    private static AnswerUniquenessReview Review(bool ambiguous) => new()
    {
        StemClear = true,
        Notes = "Evidence may be incomplete.",
        OptionVerdicts = Enumerable.Range(0, 4).Select(i => new AnswerOptionVerdict
        {
            Index = i,
            Verdict = i == 0 || (ambiguous && i == 1) ? "defensible" : "not_defensible",
            Reason = "Reason grounded in source excerpts."
        }).ToList()
    };

    [Fact]
    public void Trace_ContainsQuestionVerdictsEvidenceAndRepairAcrossFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file;
            using (var writer = new AnswerUniquenessTraceWriter(directory, RunId))
            {
                file = writer.FilePath;
                writer.Record(new("review_started", 1, "original", Question()));
                writer.Record(new("review_evidence", 1, "original",
                    Evidence: "[1] SOURCE: Biography — https://example.org/bio\nBorn in Solano."));
                writer.Record(new("review_result", 1, "original", Review: Review(true)));
                writer.Record(new("repair_started", 1, "original",
                    Question: Question(), Review: Review(true), Reason: "found 2 defensible answer choices"));

                var replacement = Question();
                replacement.Stem = "Which town was the musician born in?";
                replacement.Options = ["Solano", "Rome", "Paris", "Boston"];
                writer.Record(new("repair_output", 1, "replacement", Question: replacement));
                // Simulate a later invalid review or provider failure; previous
                // records must already be readable before Dispose.
                Assert.Equal(5, File.ReadAllLines(file).Length);
                writer.Record(new("failure", 1, "replacement", FailureKind: "review_invalid_or_unavailable"));
            }

            var lines = File.ReadAllLines(file);
            Assert.Equal(6, lines.Length);
            using var original = JsonDocument.Parse(lines[0]);
            Assert.Equal(1, original.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(RunId, original.RootElement.GetProperty("runId").GetString());
            Assert.Equal("Where was Carmen Carrozza born?",
                original.RootElement.GetProperty("diagnostic").GetProperty("question")
                    .GetProperty("stem").GetString());
            using var verdicts = JsonDocument.Parse(lines[2]);
            var options = verdicts.RootElement.GetProperty("diagnostic").GetProperty("review")
                .GetProperty("option_verdicts");
            Assert.Equal("defensible", options[0].GetProperty("verdict").GetString());
            Assert.Equal("defensible", options[1].GetProperty("verdict").GetString());

            using var failure = JsonDocument.Parse(lines[^1]);
            Assert.Equal("failure", failure.RootElement.GetProperty("diagnostic")
                .GetProperty("kind").GetString());
            Assert.Equal("review_invalid_or_unavailable", failure.RootElement
                .GetProperty("diagnostic").GetProperty("failureKind").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Trace_ClipsUntrustedContentAndKeepsAnswerIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var q = Question();
            q.Stem = new string('S', 2000);
            q.Options[0] = new string('X', 2000);
            var review = Review(false);
            review.OptionVerdicts[0].Reason = new string('R', 2000);

            using (var writer = new AnswerUniquenessTraceWriter(directory, RunId))
            {
                writer.Record(new("review_started", 1, "original", Question: q));
                writer.Record(new("review_evidence", 1, "original", Evidence: new string('E', 10000)));
                writer.Record(new("review_result", 1, "original", Review: review));
            }

            var lines = File.ReadAllLines(Path.Combine(directory, AnswerUniquenessTraceWriter.FileName(RunId)));
            using var first = JsonDocument.Parse(lines[0]);
            var question = first.RootElement.GetProperty("diagnostic").GetProperty("question");
            Assert.Equal(512, question.GetProperty("stem").GetString()!.Length);
            Assert.Equal(256, question.GetProperty("options")[0].GetString()!.Length);
            Assert.Equal(0, question.GetProperty("correct_index").GetInt32());
            using var evidence = JsonDocument.Parse(lines[1]);
            Assert.Equal(4096, evidence.RootElement.GetProperty("diagnostic")
                .GetProperty("evidence").GetString()!.Length);
            using var verdict = JsonDocument.Parse(lines[2]);
            Assert.Equal(400, verdict.RootElement.GetProperty("diagnostic").GetProperty("review")
                .GetProperty("option_verdicts")[0].GetProperty("reason").GetString()!.Length);
            Assert.True(new FileInfo(Path.Combine(directory,
                AnswerUniquenessTraceWriter.FileName(RunId))).Length < 16_384 * 3);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Trace_RejectsPathTraversalUnknownKindsAndOverwrite()
    {
        Assert.Throws<ArgumentException>(() => AnswerUniquenessTraceWriter.FileName("../outside"));
        var directory = Path.Combine(Path.GetTempPath(), "lf-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new AnswerUniquenessTraceWriter(directory, RunId);
            Assert.Throws<IOException>(() => new AnswerUniquenessTraceWriter(directory, RunId));
            Assert.Throws<InvalidOperationException>(() =>
                first.Record(new("arbitrary_model_command", 1, "original")));
            Assert.Throws<InvalidOperationException>(() =>
                first.Record(new("review_started", 31, "original")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    [Fact]
    public void PrevalidationPreview_RecoversUsefulFieldsWithoutAcceptingInvalidModelOutput()
    {
        const string missingNotes = """
            {
              "stem_clear": true,
              "option_verdicts": [
                {"index":0,"verdict":"defensible","reason":"Named town."},
                {"index":1,"verdict":"defensible","reason":"Enclosing region."}
              ]
            }
            """;
        var review = AnswerUniquenessTracePreview.Review(missingNotes);
        Assert.NotNull(review);
        Assert.Equal(2, review.OptionVerdicts.Count);
        Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<AnswerUniquenessReview>(
                missingNotes, "AnswerUniqueness Q1"));

        const string missingCitations = """
            {
              "questions": [
                {
                  "stem": "Which town was the musician born in?",
                  "options": ["Solano", "Rome", "Paris", "Boston"],
                  "correct_index": 0,
                  "explanation": "The musician was born in Solano."
                }
              ]
            }
            """;
        var candidate = AnswerUniquenessTracePreview.Replacement(missingCitations);
        Assert.NotNull(candidate);
        Assert.Equal("Solano", candidate.Options[0]);
        Assert.Empty(candidate.Citations);
        Assert.Throws<InvalidOperationException>(() =>
            StructuredAgentOutput.Parse<QuizOutput>(
                missingCitations, "AnswerUniqueness repair Q1"));

        Assert.Null(AnswerUniquenessTracePreview.Review("{invalid"));
        Assert.Null(AnswerUniquenessTracePreview.Replacement("{invalid"));
    }

}
