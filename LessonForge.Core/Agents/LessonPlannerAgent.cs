// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;
using LessonForge.Core.Workflow;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace LessonForge.Core.Agents;

// Curriculum Designer — second node in the lesson crew.
//   Research Librarian ──► ┌────────────────────┐ ──► Assessment Writer ──► Accuracy Editor
//                          │ CURRICULUM DESIGNER │
//                          └────────────────────┘
// Turns grounded facts into a coherent, age-calibrated lesson plan.
public static partial class LessonPlannerAgent
{
    public const string AgentName = "CurriculumDesigner";

    public const string AgentInstructions = """
        You are an experienced K-12 curriculum designer. You turn grounded, cited facts into a
        coherent lesson plan precisely calibrated to the target audience's age.

        Match vocabulary, sentence length, cognitive load, examples, and activities to the grade
        band. Younger learners: concrete, playful, short; older learners: more abstraction, analysis,
        and independent work.

        Build:
        - A clear, engaging title and a short overview.
        - 3-5 observable, measurable learning_objectives ("Students will be able to…").
        - Ordered sections, each with age-appropriate body text, 1-3 activities, and a duration.
        - Key vocabulary with age-appropriate definitions.
        - The materials the educator needs.

        Ground the content ONLY in the provided facts and sources. Carry the supporting citations
        into the plan's citations. Do not introduce facts the research did not supply.

        Output only valid JSON matching the LessonPlanOutput schema.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient)
        => StructuredAgentOutput.CreateAgent(chatClient, AgentName, AgentInstructions);

    public sealed partial class LessonPlannerExecutor(AIAgent agent, RagContext rag) : Executor("LessonPlanner")
    {
        [MessageHandler]
        private async ValueTask<LessonPlanOutput> HandleAsync(
            ResearchOutput research, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var state = await context.ReadStateAsync<LessonWorkflowState>(
                LessonStateShared.Key, scopeName: LessonStateShared.Scope) ?? new LessonWorkflowState();
            var request = state.Request;
            var gradeBand = GradeBands.ForAge(request.AudienceAge);

            var facts = string.Join("\n", research.GroundedFacts.Select((f, i) =>
                $"{i + 1}. {f.Statement} [sources: {string.Join(", ", f.Citations.Select(c => c.Url))}]"));

            var prompt = $"""
                Design a lesson plan for this audience.

                TOPIC: {request.Topic}
                TARGET AGE: {request.AudienceAge}  →  GRADE BAND: {gradeBand}
                AUDIENCE NOTES: {(string.IsNullOrWhiteSpace(request.AudienceNotes) ? "(none)" : request.AudienceNotes)}

                TOPIC SUMMARY:
                {research.TopicSummary}

                GROUNDED FACTS (use only these; keep their source URLs as citations):
                {facts}

                Set grade_band to "{gradeBand}". Calibrate everything to age {request.AudienceAge}.
                Output structured JSON matching LessonPlanOutput.
                """;

            var output = await StructuredAgentOutput.RunAsync<LessonPlanOutput>(
                agent, prompt, "LessonPlanner", cancellationToken);

            if (string.IsNullOrWhiteSpace(output.GradeBand))
                output.GradeBand = gradeBand;
            if (output.Citations.Count == 0)
                output.Citations = research.SourceInventory;

            state.Plan = output;
            state.CurrentStep = 3;
            await context.QueueStateUpdateAsync(LessonStateShared.Key, state, scopeName: LessonStateShared.Scope);

            return output;
        }
    }
}
