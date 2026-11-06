// Copyright (c) 2026 — LessonForge. Original work.

using System.ComponentModel;
using System.Text.Json.Serialization;

namespace LessonForge.Core.Dto;

// The data contracts the education crew passes along the pipeline:
//   LessonRequest → Research → Lesson Plan → Quiz → Verified Lesson
// Each agent reads the previous stage's output (and shared state) and produces the next.
// JSON is snake_case with [Description] hints so the LLM binds structured output reliably
// (same convention the Guild project uses in SharedFeatureData.cs).

// ─────────────────────────── Request ───────────────────────────

[Description("A request to generate a lesson plan and quiz.")]
public sealed class LessonRequest
{
    [JsonPropertyName("topic")]
    [Description("The topic or concept to teach, e.g. 'photosynthesis' or 'the causes of World War I'.")]
    public string Topic { get; set; } = "";

    [JsonPropertyName("audience_age")]
    [Description("The target audience age in years.")]
    public int AudienceAge { get; set; } = 12;

    [JsonPropertyName("audience_notes")]
    [Description("Optional extra context about the audience (prior knowledge, setting, accommodations).")]
    public string AudienceNotes { get; set; } = "";

    [JsonPropertyName("quiz_config")]
    public QuizConfig QuizConfig { get; set; } = new();
}

[Description("Configuration for the generated quiz.")]
public sealed class QuizConfig
{
    [JsonPropertyName("question_count")]
    [Description("How many multiple-choice questions to generate.")]
    public int QuestionCount { get; set; } = 10;

    [JsonPropertyName("option_count")]
    [Description("How many answer options per question.")]
    public int OptionCount { get; set; } = 4;

    [JsonPropertyName("difficulty")]
    [Description("Difficulty: 'auto' (scale to age), 'easy', 'medium', or 'hard'.")]
    public string Difficulty { get; set; } = "auto";
}

// ─────────────────────────── Shared ───────────────────────────

[Description("A source citation.")]
public sealed class CitationRef
{
    [JsonPropertyName("title")]
    [Description("The source's title or site/path.")]
    public string Title { get; set; } = "";

    [JsonPropertyName("url")]
    [Description("The source URL.")]
    public string Url { get; set; } = "";
}

// ─────────────────────── Research stage ───────────────────────

[Description("The Research Librarian's grounded findings.")]
public sealed class ResearchOutput
{
    [JsonPropertyName("topic_summary")]
    [Description("A short, neutral summary of the topic grounded in the retrieved sources.")]
    public string TopicSummary { get; set; } = "";

    [JsonPropertyName("grounded_facts")]
    [Description("Key facts, each traceable to one or more cited sources.")]
    public List<GroundedFact> GroundedFacts { get; set; } = new();

    [JsonPropertyName("queries_used")]
    [Description("The search queries used to gather facts.")]
    public List<string> QueriesUsed { get; set; } = new();

    [JsonPropertyName("source_inventory")]
    [Description("The distinct sources retrieved for this topic.")]
    public List<CitationRef> SourceInventory { get; set; } = new();
}

[Description("A single fact with its supporting citations.")]
public sealed class GroundedFact
{
    [JsonPropertyName("statement")]
    [Description("A concise, factual statement.")]
    public string Statement { get; set; } = "";

    [JsonPropertyName("citations")]
    [Description("The sources that support this statement.")]
    public List<CitationRef> Citations { get; set; } = new();
}

// ───────────────────── Lesson-plan stage ─────────────────────

[Description("The Curriculum Designer's lesson plan, calibrated to the audience age.")]
public sealed class LessonPlanOutput
{
    [JsonPropertyName("title")]
    [Description("A clear, engaging lesson title.")]
    public string Title { get; set; } = "";

    [JsonPropertyName("grade_band")]
    [Description("The grade band the plan targets, e.g. 'Grade 6-7 (age 11-12)'.")]
    public string GradeBand { get; set; } = "";

    [JsonPropertyName("overview")]
    [Description("A short overview of what the lesson covers and why it matters.")]
    public string Overview { get; set; } = "";

    [JsonPropertyName("learning_objectives")]
    [Description("What students will be able to do after the lesson (observable, measurable).")]
    public List<string> LearningObjectives { get; set; } = new();

    [JsonPropertyName("sections")]
    [Description("The ordered sections of the lesson.")]
    public List<LessonSection> Sections { get; set; } = new();

    [JsonPropertyName("vocabulary")]
    [Description("Key terms students should learn, with age-appropriate definitions.")]
    public List<VocabularyTerm> Vocabulary { get; set; } = new();

    [JsonPropertyName("materials")]
    [Description("Materials or resources the educator needs.")]
    public List<string> Materials { get; set; } = new();

    [JsonPropertyName("citations")]
    [Description("Sources the lesson content draws on.")]
    public List<CitationRef> Citations { get; set; } = new();
}

public sealed class LessonSection
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("body")]
    [Description("The section content, written at the audience's reading level.")]
    public string Body { get; set; } = "";

    [JsonPropertyName("activities")]
    [Description("Hands-on or discussion activities for this section.")]
    public List<string> Activities { get; set; } = new();

    [JsonPropertyName("duration_minutes")]
    [Description("Approximate time for this section.")]
    public int DurationMinutes { get; set; }
}

public sealed class VocabularyTerm
{
    [JsonPropertyName("term")]
    public string Term { get; set; } = "";

    [JsonPropertyName("definition")]
    public string Definition { get; set; } = "";
}

// ───────────────────────── Quiz stage ─────────────────────────

[Description("The Assessment Writer's multiple-choice quiz.")]
public sealed class QuizOutput
{
    [JsonPropertyName("questions")]
    public List<QuizQuestion> Questions { get; set; } = new();
}

[Description("A single multiple-choice question.")]
public sealed class QuizQuestion
{
    [JsonPropertyName("stem")]
    [Description("The question text.")]
    public string Stem { get; set; } = "";

    [JsonPropertyName("options")]
    [Description("The answer options. Exactly one is correct.")]
    public List<string> Options { get; set; } = new();

    [JsonPropertyName("correct_index")]
    [Description("The zero-based index of the correct option.")]
    public int CorrectIndex { get; set; }

    [JsonPropertyName("explanation")]
    [Description("Why the correct answer is right, grounded in the sources.")]
    public string Explanation { get; set; } = "";

    [JsonPropertyName("citations")]
    [Description("Sources that support the correct answer and explanation.")]
    public List<CitationRef> Citations { get; set; } = new();
}

// ──────────────────── Fact-check stage ────────────────────

[Description("The Accuracy Editor's verified, citation-checked lesson and quiz.")]
public sealed class VerifiedLessonOutput
{
    [JsonPropertyName("plan")]
    public LessonPlanOutput Plan { get; set; } = new();

    [JsonPropertyName("quiz")]
    public QuizOutput Quiz { get; set; } = new();

    [JsonPropertyName("flags")]
    [Description("Claims that could not be fully supported by the sources.")]
    public List<VerificationFlag> Flags { get; set; } = new();

    [JsonPropertyName("citation_coverage")]
    [Description("Fraction (0..1) of checked claims that are supported by at least one source.")]
    public double CitationCoverage { get; set; }

    [JsonPropertyName("notes")]
    [Description("Overall notes from the accuracy review.")]
    public string Notes { get; set; } = "";
}

[Description("A flagged, insufficiently-supported claim.")]
public sealed class VerificationFlag
{
    [JsonPropertyName("claim")]
    public string Claim { get; set; } = "";

    [JsonPropertyName("issue")]
    [Description("What is wrong or unverified about the claim.")]
    public string Issue { get; set; } = "";

    [JsonPropertyName("severity")]
    [Description("High, Medium, or Low.")]
    public string Severity { get; set; } = "Medium";
}
