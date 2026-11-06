// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
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

        Produce a complete lesson plan with these exact JSON field names:
        - title: an engaging non-empty string.
        - grade_band: an age-appropriate string supplied in the task.
        - overview: 2-3 sentences introducing the lesson.
        - learning_objectives: an array of 3-4 measurable strings.
        - sections: an array of 3-4 objects, each containing:
          title (string), body (a substantive student-facing paragraph),
          activities (array of 1-2 actionable strings), duration_minutes (integer).
        - vocabulary: an array of objects with term and definition strings.
        - materials: an array of strings.
        - citations: an array of objects with title and url strings.

        IMPORTANT: The top-level "sections" array must be nonempty. Do not rename it to
        "lesson", "outline", "content", "chapters" or "lesson_sections". Every section
        needs readable body text, not just a heading. Use learning_objectives (plural).
        Keep the output concise enough to finish the entire JSON object.

        Ground the content ONLY in the provided facts and sources. Copy citation URLs
        from those supplied; never invent a reference or introduce new historical claims.
        Output one complete JSON object, without markdown or commentary.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient, int maxOutputTokens)
        => StructuredAgentOutput.CreateSchemaAgent<LessonPlanOutput>(
            chatClient, AgentName, AgentInstructions, maxOutputTokens);

    public sealed partial class LessonPlannerExecutor(AIAgent agent, RagContext rag) : Executor("LessonPlanner")
    {
        [MessageHandler]
        private async ValueTask<LessonPlanOutput> HandleAsync(
            ResearchOutput research, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var state = await context.ReadStateAsync<LessonWorkflowState>(
                LessonStateShared.Key, scopeName: LessonStateShared.Scope) ?? new LessonWorkflowState();
            var request = state.Request;
            if (string.IsNullOrWhiteSpace(request.Topic))
                throw new InvalidOperationException(
                    "LessonPlanner cannot find the original lesson request in workflow state.");
            if (research.GroundedFacts is not { Count: > 0 })
                throw new InvalidOperationException(
                    "LessonPlanner received no approved research facts.");
            if (request.ExplorePerspectives && research.Perspectives is null)
                throw new InvalidOperationException(
                    "LessonPlanner is missing educator-approved perspective analysis.");
            if (research.Perspectives is not null)
            {
                var authoritativeSources = rag.Session is { } sources
                    ? RagPrompt.InventoryCitations(sources)
                    : throw new InvalidOperationException(
                        "LessonPlanner cannot verify perspective citations: source index is missing.");
                if (rag.PerspectiveSession is { } additional)
                    authoritativeSources.AddRange(RagPrompt.InventoryCitations(additional));
                PerspectiveAnalysisValidator.Validate(
                    research.Perspectives, authoritativeSources);
            }
            var knownSources = rag.Session is { } primarySession
                ? primarySession.Citations.Concat(
                    rag.PerspectiveSession?.Citations ?? []).ToList()
                : throw new InvalidOperationException(
                    "LessonPlanner cannot verify citations without the source index.");
            foreach (var fact in research.GroundedFacts)
                SourceCitationValidator.RequireRetrieved(
                    fact.Citations, knownSources, "Educator-approved research fact");
            SourceCitationValidator.RequireRetrieved(
                research.SourceInventory, knownSources, "Research source inventory");

            var gradeBand = GradeBands.ForAge(request.AudienceAge);

            var facts = string.Join("\n", research.GroundedFacts.Select((f, i) =>
                $"{i + 1}. {f.Statement} [sources: {string.Join("; ", f.Citations.Select(c => $"{c.Title} ({c.Url})"))}]"));
            var perspectives = PerspectivePrompt.ForLesson(research.Perspectives);
            var perspectiveInstruction = research.Perspectives?.Status == PerspectiveAnalysisValidator.Supported
                ? "Include a dedicated section comparing these credible interpretations, their supporting evidence, and limitations. Explain the distinction between perspectives and established facts. Make the activity age-appropriate. Do not assign equal credibility without evidence."
                : research.Perspectives is not null
                    ? "Do not manufacture a disagreement: the approved analysis did not establish two credible competing interpretations."
                    : "";

            // Two-dollar interpolation lets ordinary JSON braces remain literal while
            // {{expression}} still inserts the educator-approved values.
            var prompt = $$"""
                Design a lesson plan for this audience.

                TOPIC: {{request.Topic}}
                TARGET AGE: {{request.AudienceAge}}  →  GRADE BAND: {{gradeBand}}
                AUDIENCE NOTES: {{(string.IsNullOrWhiteSpace(request.AudienceNotes) ? "(none)" : request.AudienceNotes)}}

                TOPIC SUMMARY:
                {{research.TopicSummary}}

                GROUNDED FACTS (use only these; keep their source URLs as citations):
                {{facts}}

                {{perspectives}}
                {{perspectiveInstruction}}

                Set grade_band to "{{gradeBand}}" and calibrate all explanations and activities to age {{request.AudienceAge}}.
                Return one complete JSON object using exactly these top-level keys:
                title, grade_band, overview, learning_objectives, sections, vocabulary, materials, citations.

                REQUIRED STRUCTURE (the example strings below illustrate field types, not lesson content):
                {
                  "title": "Lesson title",
                  "grade_band": "Age-appropriate grade band",
                  "overview": "Two sentences introducing the topic.",
                  "learning_objectives": ["Students will be able to explain ..."],
                  "sections": [
                    {
                      "title": "Section heading",
                      "body": "Student-facing explanatory text grounded in the approved facts.",
                      "activities": ["Specific classroom activity"],
                      "duration_minutes": 15
                    }
                  ],
                  "vocabulary": [{"term": "Term", "definition": "Age-appropriate definition"}],
                  "materials": ["Classroom material"],
                  "citations": [{"title": "Source title", "url": "URL copied from the approved facts"}]
                }

                Write 3-4 complete sections with substantive bodies. Use only the approved research
                above and actual URLs from its supporting sources. Never copy the example values.
                Do not use alternate JSON keys such as "lesson_plan", "content" or "lesson_sections".
                """;

            var output = await StructuredAgentOutput.RunAsync<LessonPlanOutput>(
                agent, prompt, "LessonPlanner", cancellationToken);

            if (string.IsNullOrWhiteSpace(output.GradeBand))
                output.GradeBand = gradeBand;
            // Use the researched inventory if the model omitted its source list.
            // The inventory was collected by the retrieval system, not fabricated by an LLM.
            if (output.Citations is not { Count: > 0 })
                output.Citations = research.SourceInventory;
            SourceCitationValidator.RequireRetrieved(
                output.Citations, knownSources, "Curriculum Designer");

            // The research arriving through the RequestPort is the educator-approved
            // version, potentially edited. Preserve it over the pre-approval copy.
            state.Research = research;
            state.Plan = output;
            state.CurrentStep = 3;
            await context.QueueStateUpdateAsync(LessonStateShared.Key, state, scopeName: LessonStateShared.Scope);

            return output;
        }
    }
}
