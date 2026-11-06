using System.Text.Json;
using LessonForge.Api.Export;
using LessonForge.Api.Persistence;
using LessonForge.Core.Dto;
using Xunit;

namespace LessonForge.Tests;

public class DtoAndExportTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void LessonRequest_binds_snake_case_json()
    {
        var json = """
            {"topic":"Volcanoes","audience_age":10,"audience_notes":"visual learners",
             "quiz_config":{"question_count":8,"option_count":4,"difficulty":"easy"}}
            """;
        var req = JsonSerializer.Deserialize<LessonRequest>(json, Web)!;
        Assert.Equal("Volcanoes", req.Topic);
        Assert.Equal(10, req.AudienceAge);
        Assert.Equal(8, req.QuizConfig.QuestionCount);
        Assert.Equal("easy", req.QuizConfig.Difficulty);
    }

    [Fact]
    public void VerifiedLessonOutput_roundtrips()
    {
        var original = Sample();
        var json = JsonSerializer.Serialize(original, Web);
        var back = JsonSerializer.Deserialize<VerifiedLessonOutput>(json, Web)!;
        Assert.Equal(original.Plan.Title, back.Plan.Title);
        Assert.Equal(original.Quiz.Questions[0].CorrectIndex, back.Quiz.Questions[0].CorrectIndex);
        Assert.Contains("correct_index", json); // snake_case preserved
    }

    [Fact]
    public void Markdown_teacher_includes_answers_student_hides_them()
    {
        var view = View();
        var teacher = LessonExporter.Export(view, ExportFormat.Markdown, Audience.Teacher);
        var student = LessonExporter.Export(view, ExportFormat.Markdown, Audience.Student);

        var teacherText = System.Text.Encoding.UTF8.GetString(teacher.Content);
        var studentText = System.Text.Encoding.UTF8.GetString(student.Content);

        Assert.Contains("Answer Key", teacherText);
        Assert.Contains("✅", teacherText);
        Assert.DoesNotContain("Answer Key", studentText);
        Assert.DoesNotContain("✅", studentText);
        Assert.EndsWith(".md", teacher.FileName);
    }

    [Fact]
    public void Json_student_strips_answers()
    {
        var studentJson = System.Text.Encoding.UTF8.GetString(
            LessonExporter.Export(View(), ExportFormat.Json, Audience.Student).Content);
        Assert.DoesNotContain("correct_index", studentJson);
        Assert.DoesNotContain("explanation", studentJson);
    }

    [Theory]
    [InlineData(Audience.Teacher)]
    [InlineData(Audience.Student)]
    public void Pdf_export_produces_a_pdf_for_each_audience(Audience audience)
    {
        // Rendering both variants detects unsupported font glyphs and layout failures.
        // In particular, the teacher's correct-answer marker must work using
        // QuestPDF's bundled Lato font (no system font fallback required).
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var pdf = LessonExporter.Export(View(), ExportFormat.Pdf, audience);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.True(pdf.Content.Length > 500);
        Assert.Equal<byte[]>("%PDF"u8.ToArray(), pdf.Content[..4]);
        Assert.EndsWith(".pdf", pdf.FileName);
    }

    private static StoredLessonView View()
    {
        var sample = Sample();
        var stored = new StoredLesson(1, "Photosynthesis", 12,
            DateTime.UtcNow, new LessonRequest { Topic = "Photosynthesis", AudienceAge = 12 }, sample);
        return StoredLessonView.From(stored);
    }

    private static VerifiedLessonOutput Sample() => new()
    {
        Plan = new LessonPlanOutput
        {
            Title = "How Plants Make Food",
            GradeBand = "Grade 7 (age 12)",
            Overview = "An intro to photosynthesis.",
            LearningObjectives = ["Explain what photosynthesis is"],
            Sections = [new LessonSection { Title = "What is it?", Body = "Plants use light.", DurationMinutes = 15 }],
            Citations = [new CitationRef { Title = "Photosynthesis", Url = "https://en.wikipedia.org/wiki/Photosynthesis" }],
        },
        Quiz = new QuizOutput
        {
            Questions =
            [
                new QuizQuestion
                {
                    Stem = "What do plants use to make food?",
                    Options = ["Light", "Rocks", "Plastic", "Metal"],
                    CorrectIndex = 0,
                    Explanation = "Plants capture light energy.",
                    Citations = [new CitationRef { Title = "Photosynthesis", Url = "https://en.wikipedia.org/wiki/Photosynthesis" }],
                }
            ]
        },
        Flags = [],
        CitationCoverage = 1.0,
        Notes = "All claims supported."
    };
}
