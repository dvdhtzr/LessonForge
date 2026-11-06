// Copyright (c) 2026 — LessonForge. Original work.

using System.ComponentModel;
using System.Text.Json.Serialization;

namespace LessonForge.Core.Dto;

// The data contracts the education crew passes along the pipeline:
//   LessonRequest → Research → Lesson Plan → Quiz → Verified Lesson
// Each agent reads the previous stage's output (and shared state) and produces the next.
// JSON is snake_case with [Description] hints so the LLM binds structured output reliably.

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

    [JsonPropertyName("explore_perspectives")]
    [Description("Ask a separate agent to investigate credible differing interpretations of the topic.")]
    public bool ExplorePerspectives { get; set; }

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

/// <summary>
/// LLM-only output: keep the factual Research Librarian schema unchanged when
/// the optional Perspective Analyst is switched on or off. The workflow envelope
/// adds perspectives later, after the separate agent and educator approval.
/// </summary>
[Description("Source-grounded facts, without any model-authored perspective analysis.")]
public sealed class ResearchDraftOutput
{
    [JsonPropertyName("topic_summary")]
    public string TopicSummary { get; set; } = "";

    [JsonPropertyName("grounded_facts")]
    public List<GroundedFact> GroundedFacts { get; set; } = new();

    [JsonPropertyName("queries_used")]
    public List<string> QueriesUsed { get; set; } = new();

    [JsonPropertyName("source_inventory")]
    public List<CitationRef> SourceInventory { get; set; } = new();

    public ResearchOutput ToResearchOutput() => new()
    {
        TopicSummary = TopicSummary,
        GroundedFacts = GroundedFacts,
        QueriesUsed = QueriesUsed,
        SourceInventory = SourceInventory
    };
}

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

    [JsonPropertyName("perspectives")]
    [Description("Optional educator-reviewed analysis of credible differing interpretations.")]
    public PerspectiveAnalysisOutput? Perspectives { get; set; }
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

// ─────────────── Optional perspective-analysis stage ───────────────

/// <summary>
/// A separate evidence-aware MAF agent produces this optional review. A conclusion that
/// no meaningful disagreement is established is a valid result, not a reason to invent sides.
/// </summary>
[Description("Credible perspectives and the evidence supporting or limiting them.")]
public sealed class PerspectiveAnalysisOutput
{
    [JsonPropertyName("status")]
    [Description("supported_disagreement, no_substantive_disagreement, or insufficient_evidence.")]
    public string Status { get; set; } = "";

    [JsonPropertyName("perspectives")]
    [Description("Two or three sourced interpretations only when supported_disagreement.")]
    public List<PerspectiveViewpoint> Perspectives { get; set; } = new();

    [JsonPropertyName("areas_of_agreement")]
    [Description("Evidence-backed observations or facts the credible interpretations share.")]
    public List<string> AreasOfAgreement { get; set; } = new();

    [JsonPropertyName("evidence_limitations")]
    [Description("Why evidence is insufficient, uncertain, disputed or unevenly distributed.")]
    public string EvidenceLimitations { get; set; } = "";

    [JsonPropertyName("discussion_questions")]
    [Description("Age-appropriate prompts for comparing evidence rather than demanding agreement.")]
    public List<string> DiscussionQuestions { get; set; } = new();
}

[Description("One sourced viewpoint or scholarly interpretation.")]
public sealed class PerspectiveViewpoint
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("interpretation")]
    public string Interpretation { get; set; } = "";

    [JsonPropertyName("evidence_basis")]
    public string EvidenceBasis { get; set; } = "";

    [JsonPropertyName("limitations")]
    public string Limitations { get; set; } = "";

    [JsonPropertyName("citations")]
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

/// <summary>
/// Compact model-produced review only. The application joins these findings with
/// the educator-approved plan and generated quiz; the LLM never rewrites either.
/// </summary>
[Description("Accuracy Editor findings about an existing lesson and quiz, not rewritten content.")]
public sealed class FactCheckReviewOutput
{
    [JsonPropertyName("flags")]
    [Description("Unsupported or misleading claims; use an empty array if none were found.")]
    public List<VerificationFlag> Flags { get; set; } = new();

    [JsonPropertyName("citation_coverage")]
    [Description("Estimated fraction from 0 to 1 of reviewed claims supported by the evidence.")]
    public double CitationCoverage { get; set; }

    [JsonPropertyName("notes")]
    [Description("Short neutral explanation of results, evidence limitations, and uncertainty.")]
    public string Notes { get; set; } = "";
}

[Description("The Accuracy Editor's verified, citation-checked lesson and quiz.")]
public sealed class VerifiedLessonOutput
{
    [JsonPropertyName("plan")]
    public LessonPlanOutput Plan { get; set; } = new();

    [JsonPropertyName("perspectives")]
    [Description("Educator-approved analysis when the optional perspectives mode was requested.")]
    public PerspectiveAnalysisOutput? Perspectives { get; set; }

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
