// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>Bounded prompt context shared by the downstream educational agents.</summary>
public static class PerspectivePrompt
{
    public static string ForLesson(PerspectiveAnalysisOutput? analysis)
    {
        if (analysis is null) return "";

        static string Short(string? value, int max)
            => string.IsNullOrWhiteSpace(value) ? "" : value.Length <= max ? value : value[..max] + "…";

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("EDUCATOR-APPROVED PERSPECTIVE ANALYSIS:");
        sb.AppendLine($"Status: {analysis.Status}");
        sb.AppendLine($"Evidence limitations: {Short(analysis.EvidenceLimitations, 340)}");

        if (analysis.Status != PerspectiveAnalysisValidator.Supported)
        {
            sb.AppendLine("Do not invent a debate or opposing sides. Explain, if relevant, why the supplied evidence does not establish differing interpretations.");
            return sb.ToString();
        }

        sb.AppendLine("Credible interpretations (not necessarily equally supported):");
        foreach (var view in analysis.Perspectives.Take(3))
        {
            sb.AppendLine($"- {Short(view.Name, 90)}: {Short(view.Interpretation, 260)}");
            sb.AppendLine($"  Supporting evidence: {Short(view.EvidenceBasis, 230)}");
            sb.AppendLine($"  Limitations: {Short(view.Limitations, 170)}");
            sb.AppendLine($"  Sources: {string.Join("; ", view.Citations.Take(2).Select(c => $"{Short(c.Title, 90)} ({c.Url})"))}");
        }

        if (analysis.AreasOfAgreement.Count > 0)
        {
            sb.AppendLine("Points of agreement:");
            foreach (var item in analysis.AreasOfAgreement.Take(3))
                sb.AppendLine($"- {Short(item, 160)}");
        }
        if (analysis.DiscussionQuestions.Count > 0)
        {
            sb.AppendLine("Suggested evidence-focused student questions:");
            foreach (var item in analysis.DiscussionQuestions.Take(3))
                sb.AppendLine($"- {Short(item, 180)}");
        }

        sb.AppendLine("Explain why interpretations differ, weigh the evidence, and keep established facts distinct. Do not present weak claims as equally substantiated.");
        return sb.ToString();
    }
}
