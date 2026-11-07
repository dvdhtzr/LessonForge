// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
using LessonForge.Core.Workflow;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace LessonForge.Core.Agents;

// Assessment Writer — third node in the lesson crew.
//   … ──► ┌──────────────────┐ ──► Accuracy Editor
//         │ ASSESSMENT WRITER │
//         └──────────────────┘
// Writes fair, unambiguous multiple-choice questions scaled to the audience.
public static partial class QuizGeneratorAgent
{
    public const string AgentName = "AssessmentWriter";

    public const string AgentInstructions = """
        You are an assessment specialist trained in item-writing best practices. You write fair,
        unambiguous multiple-choice questions that measure the lesson's learning objectives.

        Rules:
        - Exactly one option is correct; set correct_index to its zero-based position.
        - Distractors are plausible and similar in length/style — no joke options, no "all of the
          above" or "none of the above", no trick wording.
        - Each question maps to the approved lesson content and is answerable from it.
        - Scale difficulty to the audience age; vary across recall, understanding, and application.
        - Explanations must be concise, accurate, and grounded in the provided lesson.
        - Only cite supplied sources. Do not invent URLs or facts.

        Return one JSON object with exactly one top-level property, "questions".
        "questions" MUST be a nonempty array of question objects. Each question has:
        - "stem": a nonempty question string
        - "options": an array of answer strings in the requested count
        - "correct_index": a zero-based integer pointing at the correct option
        - "explanation": a short factual explanation supporting the correct answer
        - "citations": a nonempty array of objects with "title" and "url", copied from
          AVAILABLE SOURCES in the prompt (one strong citation per question is enough).

        Do not use alternative top-level keys such as "quiz", "assessment", "items",
        or "multiple_choice_questions". Finish the entire JSON object. No markdown.
        Keep each question and explanation short to stay within the model's output budget.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient, int maxOutputTokens)
        => StructuredAgentOutput.CreateSchemaAgent<QuizOutput>(
            chatClient, AgentName, AgentInstructions, maxOutputTokens);

    public sealed partial class QuizGeneratorExecutor(AIAgent agent, RagContext rag) : Executor("QuizGenerator")
    {
        [MessageHandler]
        private async ValueTask<QuizOutput> HandleAsync(
            LessonPlanOutput plan, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var state = await context.ReadStateAsync<LessonWorkflowState>(
                LessonStateShared.Key, scopeName: LessonStateShared.Scope) ?? new LessonWorkflowState();
            var request = state.Request;
            if (string.IsNullOrWhiteSpace(request.Topic))
                throw new InvalidOperationException(
                    "QuizGenerator cannot find the original lesson request in workflow state.");
            if (plan.Sections is not { Count: > 0 })
                throw new InvalidOperationException(
                    "QuizGenerator received no approved lesson sections.");

            var cfg = request.QuizConfig;
            if (cfg.QuestionCount is < 1 or > 30 || cfg.OptionCount is < 2 or > 6)
                throw new InvalidOperationException(
                    "QuizGenerator requires a question count between 1 and 30 and " +
                    "an option count between 2 and 6.");

            var difficulty = string.Equals(cfg.Difficulty, "auto", StringComparison.OrdinalIgnoreCase)
                ? $"scaled to age {request.AudienceAge}"
                : cfg.Difficulty;

            var sb = new StringBuilder();
            sb.AppendLine($"LESSON: {plan.Title}");
            sb.AppendLine($"GRADE BAND: {plan.GradeBand}");
            sb.AppendLine("OBJECTIVES:");
            foreach (var o in plan.LearningObjectives) sb.AppendLine($" - {o}");
            sb.AppendLine("CONTENT:");
            foreach (var s in plan.Sections)
                sb.AppendLine($" ## {s.Title}\n{s.Body}");

            var knownSources = rag.Session is { } primarySession
                ? primarySession.Citations.Concat(
                    rag.PerspectiveSession?.Citations ?? []).ToList()
                : throw new InvalidOperationException(
                    "QuizGenerator cannot verify citations without the retrieval index.");
            var citations = plan.Citations.Count > 0 ? plan.Citations : state.Research?.SourceInventory ?? new();
            SourceCitationValidator.RequireRetrieved(citations, knownSources, "Educator-approved lesson");
            SourceCitationDeduplicator.NormalizeInPlace(citations);
            var citationList = string.Join("\n", citations.Select(c => $" - {c.Title}: {c.Url}"));
            var approvedPerspectives = state.Research?.Perspectives;
            if (request.ExplorePerspectives && approvedPerspectives is null)
                throw new InvalidOperationException(
                    "QuizGenerator is missing the educator-approved perspective analysis.");
            var perspectiveContext = PerspectivePrompt.ForLesson(approvedPerspectives);
            var perspectiveInstruction = approvedPerspectives?.Status == PerspectiveAnalysisValidator.Supported
                ? "Include at least one age-appropriate question requiring students to compare the evidence for different interpretations, not to choose which worldview they personally endorse. Cite the supplied sources."
                : approvedPerspectives is not null
                    ? "Do not create an artificial debate question; the reviewed evidence does not establish substantive disagreement."
                    : "";

            var prompt = $"""
                Write a multiple-choice quiz for this lesson.

                {sb}

                AVAILABLE SOURCES (cite by URL):
                {citationList}

                {perspectiveContext}
                {perspectiveInstruction}

                Produce exactly {cfg.QuestionCount} questions, each with exactly {cfg.OptionCount}
                options. Difficulty: {difficulty}.

                Return a single complete JSON object with a top-level "questions" array.
                Each array item must have exactly these fields: "stem", "options",
                "correct_index", "explanation", and "citations". Each citation is an
                object containing "title" and "url" copied from AVAILABLE SOURCES.
                Every question requires one concise, supporting citation; never invent
                source URLs. Keep stems and explanations brief. No extra JSON wrappers.
                """;

            var output = await StructuredAgentOutput.RunAsync<QuizOutput>(
                agent, prompt, "QuizGenerator", cancellationToken);
            ValidateCounts(output, cfg);
            foreach (var question in output.Questions)
                SourceCitationValidator.RequireRetrieved(
                    question.Citations, knownSources, "Assessment question");
            SourceCitationDeduplicator.Normalize(output);

            // The plan arriving through the RequestPort is the educator-approved
            // version, potentially edited. The final Accuracy Editor must see it.
            state.Plan = plan;
            state.Quiz = output;
            state.CurrentStep = 4;
            await context.QueueStateUpdateAsync(LessonStateShared.Key, state, scopeName: LessonStateShared.Scope);

            return output;
        }
    }

    /// <summary>
    /// The requested question and answer-option counts are runtime constraints and cannot
    /// be captured by the static QuizOutput JSON schema. Reject short or malformed quizzes
    /// rather than silently changing the educator's requested assessment.
    /// </summary>
    public static void ValidateCounts(QuizOutput quiz, QuizConfig config)
    {
        ArgumentNullException.ThrowIfNull(quiz);
        ArgumentNullException.ThrowIfNull(config);

        if (quiz.Questions is null || quiz.Questions.Count != config.QuestionCount)
            throw new InvalidOperationException(
                $"QuizGenerator returned {quiz.Questions?.Count ?? 0} questions, " +
                $"but {config.QuestionCount} were requested.");

        for (var i = 0; i < quiz.Questions.Count; i++)
        {
            var question = quiz.Questions[i];
            if (question is null || question.Options is null || question.Options.Count != config.OptionCount)
                throw new InvalidOperationException(
                    $"QuizGenerator question {i + 1} has {question?.Options?.Count ?? 0} options, " +
                    $"but {config.OptionCount} were requested.");
        }
    }
}