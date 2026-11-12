// Copyright (c) 2026 — LessonForge. Original work.
using System.Text.Json;
using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>
/// Optional evaluation-only trace event. A trace is diagnostic evidence about
/// a model's judgments, not a validation result and never a user-facing report.
/// The API host writes bounded snapshots to an explicitly enabled local file.
/// </summary>
public sealed record AnswerUniquenessTraceEvent(
    string Kind,
    int QuestionNumber,
    string Attempt,
    QuizQuestion? Question = null,
    AnswerUniquenessReview? Review = null,
    string? Evidence = null,
    string? Reason = null,
    string? FailureKind = null);

/// <summary>
/// Best-effort, shape-only preview of a model answer before strict validation.
/// Never treats this partial payload as valid: it exists solely so a failed
/// benchmark can show which specific answers or verdicts the model attempted.
/// Malformed JSON is not stored raw.
/// </summary>
public static class AnswerUniquenessTracePreview
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static AnswerUniquenessReview? Review(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<AnswerUniquenessReview>(raw, s_json); }
        catch (JsonException) { return null; }
    }

    public static QuizQuestion? Replacement(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<QuizOutput>(raw, s_json)?.Questions?.FirstOrDefault(); }
        catch (JsonException) { return null; }
    }
}
