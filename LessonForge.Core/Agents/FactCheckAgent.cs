// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
using LessonForge.Core.Workflow;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace LessonForge.Core.Agents;

// Accuracy Editor — final node in the lesson crew.
//   … ──► ┌────────────────┐
//         │ ACCURACY EDITOR │ ──► verified lesson + quiz
//         └────────────────┘
// Verifies every claim and quiz answer against the retrieved sources, repairs/attaches citations,
// and flags anything unsupported.
public static partial class FactCheckAgent
{
    public const string AgentName = "AccuracyEditor";

    public const string AgentInstructions = """
        You are a skeptical fact-checking editor. You verify a lesson plan and its quiz against the
        retrieved SOURCE excerpts.

        For each learning objective, key claim, and quiz answer:
        - Confirm it is supported by the provided sources. Attach or repair citations (URLs) so each
          supported claim points to the source that backs it.
        - If a claim is not supported by any provided source, add it to flags with a clear issue and
          a severity (High/Medium/Low). Do NOT delete content — flag it so the educator can decide.

        Return the full plan and quiz (corrected citations only — keep the educator-approved content
        intact), the flags, a citation_coverage between 0 and 1 (supported claims ÷ checked claims),
        and short overall notes. You would rather flag a gap than let an unsourced claim ship.

        Output only valid JSON matching the VerifiedLessonOutput schema.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient, int maxOutputTokens)
        => StructuredAgentOutput.CreateAgent(chatClient, AgentName, AgentInstructions, maxOutputTokens);

    public sealed partial class FactCheckExecutor(AIAgent agent, RagContext rag) : Executor("FactCheck")
    {
        [MessageHandler]
        private async ValueTask<VerifiedLessonOutput> HandleAsync(
            QuizOutput quiz, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var state = await context.ReadStateAsync<LessonWorkflowState>(
                LessonStateShared.Key, scopeName: LessonStateShared.Scope) ?? new LessonWorkflowState();
            var plan = state.Plan ?? new LessonPlanOutput();

            // Pull supporting evidence for the objectives and quiz stems.
            var evidence = "(no retrieval index available)";
            if (rag.Session is { } session)
            {
                var queries = plan.LearningObjectives
                    .Concat(quiz.Questions.Select(q => q.Stem))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(12);
                evidence = await RagPrompt.GatherEvidenceAsync(session, queries, perQuery: 4, maxSnippets: 20, ct: cancellationToken);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"LESSON TITLE: {plan.Title}");
            sb.AppendLine("OBJECTIVES:");
            foreach (var o in plan.LearningObjectives) sb.AppendLine($" - {o}");
            sb.AppendLine("SECTIONS:");
            foreach (var s in plan.Sections) sb.AppendLine($" ## {s.Title}\n{s.Body}");
            sb.AppendLine("QUIZ:");
            for (var i = 0; i < quiz.Questions.Count; i++)
            {
                var q = quiz.Questions[i];
                var correct = q.CorrectIndex >= 0 && q.CorrectIndex < q.Options.Count ? q.Options[q.CorrectIndex] : "(?)";
                sb.AppendLine($" Q{i + 1}: {q.Stem}  → correct: {correct}");
            }

            var prompt = $"""
                Verify this lesson and quiz against the sources.

                MATERIAL:
                {sb}

                SOURCE EXCERPTS (the only evidence; cite by URL):
                {evidence}

                Return the full plan and quiz with citations corrected to point at supporting sources,
                a flags list for anything unsupported, a citation_coverage (0..1), and notes.
                Output structured JSON matching VerifiedLessonOutput.
                """;

            var output = await StructuredAgentOutput.RunAsync<VerifiedLessonOutput>(
                agent, prompt, "FactCheck", cancellationToken);

            // Guard against a model that drops content: fall back to the approved plan/quiz.
            if (output.Plan is null || output.Plan.Sections.Count == 0)
                output.Plan = plan;
            if (output.Quiz is null || output.Quiz.Questions.Count == 0)
                output.Quiz = quiz;

            await context.QueueStateUpdateAsync(LessonStateShared.Key, state, scopeName: LessonStateShared.Scope);
            return output;
        }
    }
}
