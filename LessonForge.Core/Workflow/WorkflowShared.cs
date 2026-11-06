// Copyright (c) 2026 — LessonForge. Original work.

using System.Text.Json.Serialization;
using LessonForge.Core.Dto;
using LessonForge.Core.Rag;

namespace LessonForge.Core.Workflow;

/// <summary>
/// Serializable per-run state carried in the MAF workflow's state store, so each stage can read the
/// original request and upstream outputs without them all being on the message edge.
/// </summary>
public sealed class LessonWorkflowState
{
    [JsonPropertyName("request")]
    public LessonRequest Request { get; set; } = new();

    [JsonPropertyName("research")]
    public ResearchOutput? Research { get; set; }

    [JsonPropertyName("plan")]
    public LessonPlanOutput? Plan { get; set; }

    [JsonPropertyName("quiz")]
    public QuizOutput? Quiz { get; set; }

    [JsonPropertyName("current_step")]
    public int CurrentStep { get; set; }
}

/// <summary>Keys for the workflow state-store entry the crew shares.</summary>
public static class LessonStateShared
{
    public const string Scope = "LessonStateScope";
    public const string Key = "singleton";
}

/// <summary>
/// A non-serializable, per-run holder for the live <see cref="RetrievalSession"/>. The vector index
/// can't live in the (JSON) workflow state, so a single instance of this is constructed per run and
/// shared by every executor: the Research stage sets <see cref="Session"/>, the downstream stages
/// read it to search for supporting evidence.
/// </summary>
public sealed class RagContext
{
    public RetrievalSession? Session { get; set; }

    /// <summary>Additional retrieved citations used by the optional Perspective Analyst.</summary>
    public RetrievalSession? PerspectiveSession { get; set; }
}

/// <summary>Maps an audience age to a human-readable grade band for lesson calibration.</summary>
public static class GradeBands
{
    public static string ForAge(int age) => age switch
    {
        <= 5 => "Pre-K / Kindergarten (age 4-5)",
        6 => "Grade 1 (age 6)",
        7 => "Grade 2 (age 7)",
        8 => "Grade 3 (age 8)",
        9 => "Grade 4 (age 9)",
        10 => "Grade 5 (age 10)",
        11 => "Grade 6 (age 11)",
        12 => "Grade 7 (age 12)",
        13 => "Grade 8 (age 13)",
        14 => "Grade 9 (age 14)",
        15 => "Grade 10 (age 15)",
        16 => "Grade 11 (age 16)",
        17 => "Grade 12 (age 17)",
        18 or 19 => "Early college / Grade 13 (age 18-19)",
        _ => $"Adult learner (age {age})"
    };
}
