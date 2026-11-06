// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using System.Text.Json;
using LessonForge.Api.Persistence;
using LessonForge.Core.Dto;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LessonForge.Api.Export;

public enum ExportFormat { Markdown, Json, Pdf }

/// <summary>Student handout (no answers) vs teacher copy (answers + explanations + citations).</summary>
public enum Audience { Student, Teacher }

public sealed record ExportResult(byte[] Content, string ContentType, string FileName);

/// <summary>
/// Renders a verified lesson + quiz to Markdown, JSON, or PDF, in a student or teacher variant.
/// The student variant omits correct answers, explanations, and the answer key.
/// </summary>
public static class LessonExporter
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ExportResult Export(StoredLessonView lesson, ExportFormat format, Audience audience) => format switch
    {
        ExportFormat.Markdown => new(Encoding.UTF8.GetBytes(Markdown(lesson, audience)), "text/markdown; charset=utf-8", Name(lesson, audience, "md")),
        ExportFormat.Json => new(Encoding.UTF8.GetBytes(Json(lesson, audience)), "application/json; charset=utf-8", Name(lesson, audience, "json")),
        ExportFormat.Pdf => new(Pdf(lesson, audience), "application/pdf", Name(lesson, audience, "pdf")),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static string Name(StoredLessonView l, Audience a, string ext)
    {
        var slug = new string((l.Plan.Title.Length > 0 ? l.Plan.Title : l.Topic)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        slug = string.Join("-", slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 60) slug = slug[..60];
        return $"{(slug.Length == 0 ? "lesson" : slug)}-{a.ToString().ToLowerInvariant()}.{ext}";
    }

    // ───────────────────────── Markdown ─────────────────────────

    private static string Markdown(StoredLessonView l, Audience audience)
    {
        var p = l.Plan;
        var sb = new StringBuilder();
        sb.AppendLine($"# {p.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Grade band:** {p.GradeBand}  ");
        sb.AppendLine($"**Audience age:** {l.AudienceAge}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(p.Overview))
        {
            sb.AppendLine("## Overview");
            sb.AppendLine(p.Overview);
            sb.AppendLine();
        }
        if (p.LearningObjectives.Count > 0)
        {
            sb.AppendLine("## Learning Objectives");
            foreach (var o in p.LearningObjectives) sb.AppendLine($"- {o}");
            sb.AppendLine();
        }
        if (p.Materials.Count > 0)
        {
            sb.AppendLine("## Materials");
            foreach (var m in p.Materials) sb.AppendLine($"- {m}");
            sb.AppendLine();
        }
        foreach (var s in p.Sections)
        {
            sb.AppendLine($"## {s.Title}" + (s.DurationMinutes > 0 ? $" ({s.DurationMinutes} min)" : ""));
            sb.AppendLine(s.Body);
            if (s.Activities.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Activities:**");
                foreach (var a in s.Activities) sb.AppendLine($"- {a}");
            }
            sb.AppendLine();
        }
        if (p.Vocabulary.Count > 0)
        {
            sb.AppendLine("## Vocabulary");
            foreach (var v in p.Vocabulary) sb.AppendLine($"- **{v.Term}** — {v.Definition}");
            sb.AppendLine();
        }

        if (l.Perspectives is { } analysis)
        {
            sb.AppendLine("## Perspective Analysis");
            sb.AppendLine(analysis.Status switch
            {
                "supported_disagreement" => "Credible differing interpretations have been identified.",
                "no_substantive_disagreement" => "No substantive disagreement was established from the available evidence.",
                _ => "The available evidence was insufficient to establish competing interpretations."
            });
            sb.AppendLine();
            foreach (var view in analysis.Perspectives)
            {
                sb.AppendLine($"### {view.Name}");
                sb.AppendLine(view.Interpretation);
                sb.AppendLine($"**Supporting evidence:** {view.EvidenceBasis}");
                sb.AppendLine($"**Limitations:** {view.Limitations}");
                foreach (var citation in view.Citations)
                    sb.AppendLine($"- [{citation.Title}]({citation.Url})");
                sb.AppendLine();
            }
            if (analysis.AreasOfAgreement.Count > 0)
            {
                sb.AppendLine("### Areas of agreement");
                foreach (var agreement in analysis.AreasOfAgreement)
                    sb.AppendLine($"- {agreement}");
            }
            sb.AppendLine($"**Evidence limitations:** {analysis.EvidenceLimitations}");
            if (analysis.DiscussionQuestions.Count > 0)
            {
                sb.AppendLine("### Discussion questions");
                foreach (var question in analysis.DiscussionQuestions)
                    sb.AppendLine($"- {question}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Quiz");
        sb.AppendLine();
        var questions = l.Quiz.Questions;
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            sb.AppendLine($"**{i + 1}. {q.Stem}**");
            sb.AppendLine();
            for (var o = 0; o < q.Options.Count; o++)
            {
                var marker = audience == Audience.Teacher && o == q.CorrectIndex ? " ✅" : "";
                sb.AppendLine($"- {(char)('A' + o)}. {q.Options[o]}{marker}");
            }
            if (audience == Audience.Teacher)
            {
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(q.Explanation))
                    sb.AppendLine($"> **Why:** {q.Explanation}");
                if (q.Citations.Count > 0)
                    sb.AppendLine($"> **Sources:** {string.Join("; ", q.Citations.Select(c => $"[{c.Title}]({c.Url})"))}");
            }
            sb.AppendLine();
        }

        if (audience == Audience.Teacher)
        {
            sb.AppendLine("### Answer Key");
            sb.AppendLine(string.Join("  ", questions.Select((q, i) =>
                $"{i + 1}:{(q.CorrectIndex >= 0 && q.CorrectIndex < q.Options.Count ? (char)('A' + q.CorrectIndex) : '?')}")));
            sb.AppendLine();

            if (l.Flags.Count > 0)
            {
                sb.AppendLine("### Accuracy Flags");
                foreach (var f in l.Flags)
                {
                    var location = string.IsNullOrWhiteSpace(f.Location) ? "" : $" {f.Location}";
                    var category = string.IsNullOrWhiteSpace(f.Category) ? "" : $" ({f.Category.Replace('_', ' ')})";
                    sb.AppendLine($"- **[{f.Severity}]**{location}{category}: {f.Claim} — {f.Issue}");
                    if (!string.IsNullOrWhiteSpace(f.Recommendation))
                        sb.AppendLine($"  - **Recommended action:** {f.Recommendation}");
                    if (!string.IsNullOrWhiteSpace(f.Evidence))
                        sb.AppendLine($"  - **AI evidence summary:** {f.Evidence}");
                }
                sb.AppendLine();
            }

            var cites = p.Citations;
            if (cites.Count > 0)
            {
                sb.AppendLine("### Sources");
                foreach (var c in cites) sb.AppendLine($"- [{c.Title}]({c.Url})");
            }
        }

        return sb.ToString();
    }

    // ───────────────────────── JSON ─────────────────────────

    private static string Json(StoredLessonView l, Audience audience)
    {
        if (audience == Audience.Teacher)
            return JsonSerializer.Serialize(new { l.Plan, l.Perspectives, l.Quiz, l.Flags, l.CitationCoverage }, s_json);

        // Student: strip answers, explanations, citations, flags.
        var studentQuiz = l.Quiz.Questions.Select(q => new { q.Stem, q.Options }).ToList();
        return JsonSerializer.Serialize(new { l.Plan, l.Perspectives, Quiz = new { Questions = studentQuiz } }, s_json);
    }

    // ───────────────────────── PDF ─────────────────────────

    private static byte[] Pdf(StoredLessonView l, Audience audience)
    {
        var p = l.Plan;
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(40);
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(x => x.FontSize(11).LineHeight(1.3f));

                page.Header().Column(col =>
                {
                    col.Item().Text(p.Title).FontSize(20).Bold();
                    col.Item().Text($"{p.GradeBand}  •  {(audience == Audience.Teacher ? "Teacher copy" : "Student handout")}")
                        .FontSize(10).FontColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Spacing(10);

                    if (!string.IsNullOrWhiteSpace(p.Overview))
                        Section(col, "Overview", c => c.Item().Text(p.Overview));

                    if (p.LearningObjectives.Count > 0)
                        Section(col, "Learning Objectives", c => Bullets(c, p.LearningObjectives));

                    foreach (var s in p.Sections)
                    {
                        Section(col, s.Title + (s.DurationMinutes > 0 ? $" ({s.DurationMinutes} min)" : ""), c =>
                        {
                            c.Item().Text(s.Body);
                            if (s.Activities.Count > 0)
                            {
                                c.Item().PaddingTop(4).Text("Activities").Bold();
                                Bullets(c, s.Activities);
                            }
                        });
                    }

                    if (p.Vocabulary.Count > 0)
                        Section(col, "Vocabulary", c => Bullets(c, p.Vocabulary.Select(v => $"{v.Term} — {v.Definition}")));

                    if (l.Perspectives is { } analysis)
                        Section(col, "Perspective Analysis", c =>
                        {
                            var status = analysis.Status switch
                            {
                                "supported_disagreement" => "Credible interpretations differ.",
                                "no_substantive_disagreement" => "No substantive disagreement was established.",
                                _ => "Available evidence is insufficient to establish differing interpretations."
                            };
                            c.Item().Text(status);
                            foreach (var view in analysis.Perspectives)
                            {
                                c.Item().PaddingTop(4).Text(view.Name).Bold();
                                c.Item().Text(view.Interpretation);
                                c.Item().Text($"Supporting evidence: {view.EvidenceBasis}");
                                c.Item().Text($"Limitations: {view.Limitations}").FontSize(9);
                                Bullets(c, view.Citations.Select(ci => $"{ci.Title}: {ci.Url}"));
                            }
                            if (analysis.AreasOfAgreement.Count > 0)
                            {
                                c.Item().Text("Areas of agreement").Bold();
                                Bullets(c, analysis.AreasOfAgreement);
                            }
                            c.Item().Text($"Evidence limitations: {analysis.EvidenceLimitations}").FontSize(9);
                            if (analysis.DiscussionQuestions.Count > 0)
                            {
                                c.Item().Text("Discussion questions").Bold();
                                Bullets(c, analysis.DiscussionQuestions);
                            }
                        });

                    Section(col, "Quiz", c =>
                    {
                        var questions = l.Quiz.Questions;
                        for (var i = 0; i < questions.Count; i++)
                        {
                            var q = questions[i];
                            c.Item().PaddingTop(6).Text($"{i + 1}. {q.Stem}").Bold();
                            for (var o = 0; o < q.Options.Count; o++)
                            {
                                var correct = audience == Audience.Teacher && o == q.CorrectIndex;
                                // QuestPDF's bundled Lato font lacks U+2713 (checkmark). Use a
                                // readable ASCII label instead of relying on machine-specific fonts.
                                var text = $"   {(char)('A' + o)}. {q.Options[o]}" + (correct ? "  (correct answer)" : "");
                                var item = c.Item().Text(text);
                                if (correct) item.Bold();
                            }
                            if (audience == Audience.Teacher && !string.IsNullOrWhiteSpace(q.Explanation))
                                c.Item().Text($"Why: {q.Explanation}").FontSize(9).Italic().FontColor(Colors.Grey.Darken2);
                        }
                    });

                    if (audience == Audience.Teacher)
                    {
                        var questions = l.Quiz.Questions;
                        Section(col, "Answer Key", c => c.Item().Text(string.Join("   ", questions.Select((q, i) =>
                            $"{i + 1}:{(q.CorrectIndex >= 0 && q.CorrectIndex < q.Options.Count ? (char)('A' + q.CorrectIndex) : '?')}"))));

                        if (l.Flags.Count > 0)
                            Section(col, "Accuracy Flags", c => Bullets(c, l.Flags.Select(f =>
                                $"[{f.Severity}] {f.Location} {f.Claim} — {f.Issue}" +
                                (string.IsNullOrWhiteSpace(f.Recommendation) ? "" : $" Action: {f.Recommendation}"))));

                        if (p.Citations.Count > 0)
                            Section(col, "Sources", c => Bullets(c, p.Citations.Select(ci => $"{ci.Title}: {ci.Url}")));
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Generated by LessonForge • page ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void Section(ColumnDescriptor col, string heading, Action<ColumnDescriptor> body)
    {
        col.Item().Column(c =>
        {
            c.Item().PaddingTop(4).Text(heading).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            body(c);
        });
    }

    private static void Bullets(ColumnDescriptor col, IEnumerable<string> items)
    {
        foreach (var item in items)
            col.Item().Row(r =>
            {
                r.ConstantItem(12).Text("•");
                r.RelativeItem().Text(item);
            });
    }
}

/// <summary>A flattened, serialization-friendly view the exporter renders from.</summary>
public sealed record StoredLessonView(
    string Topic, int AudienceAge, LessonPlanOutput Plan, QuizOutput Quiz,
    IReadOnlyList<VerificationFlag> Flags, double CitationCoverage,
    PerspectiveAnalysisOutput? Perspectives = null)
{
    public static StoredLessonView From(StoredLesson stored) => new(
        stored.Topic, stored.AudienceAge, stored.Result.Plan, stored.Result.Quiz,
        stored.Result.Flags, stored.Result.CitationCoverage, stored.Result.Perspectives);
}
