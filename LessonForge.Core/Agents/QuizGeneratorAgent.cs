// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using LessonForge.Core.Dto;
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
        - Each question maps to the lesson content and is answerable from it.
        - Scale difficulty to the audience age; vary across recall, understanding, and application.
        - Every question includes an explanation of why the correct answer is right, grounded in the
          sources, with citations drawn from the lesson/research sources.

        Output only valid JSON matching the QuizOutput schema.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient, int maxOutputTokens)
        => StructuredAgentOutput.CreateAgent(chatClient, AgentName, AgentInstructions, maxOutputTokens);

    public sealed partial class QuizGeneratorExecutor(AIAgent agent, RagContext rag) : Executor("QuizGenerator")
    {
        [MessageHandler]
        private async ValueTask<QuizOutput> HandleAsync(
            LessonPlanOutput plan, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var state = await context.ReadStateAsync<LessonWorkflowState>(
                LessonStateShared.Key, scopeName: LessonStateShared.Scope) ?? new LessonWorkflowState();
            var request = state.Request;
            var cfg = request.QuizConfig;
            var difficulty = cfg.Difficulty.Equals("auto", StringComparison.OrdinalIgnoreCase)
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

            var citations = plan.Citations.Count > 0 ? plan.Citations : state.Research?.SourceInventory ?? new();
            var citationList = string.Join("\n", citations.Select(c => $" - {c.Title}: {c.Url}"));

            var prompt = $"""
                Write a multiple-choice quiz for this lesson.

                {sb}

                AVAILABLE SOURCES (cite by URL):
                {citationList}

                Produce exactly {cfg.QuestionCount} questions, each with exactly {cfg.OptionCount}
                options. Difficulty: {difficulty}. Each question needs a correct_index, an
                explanation grounded in the sources, and citations. Output structured JSON.
                """;

            var output = await StructuredAgentOutput.RunAsync<QuizOutput>(
                agent, prompt, "QuizGenerator", cancellationToken);

            state.Quiz = output;
            state.CurrentStep = 4;
            await context.QueueStateUpdateAsync(LessonStateShared.Key, state, scopeName: LessonStateShared.Scope);

            return output;
        }
    }
}
