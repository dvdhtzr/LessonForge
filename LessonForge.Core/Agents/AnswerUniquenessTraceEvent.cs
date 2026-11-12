// Copyright (c) 2026 — LessonForge. Original work.
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
