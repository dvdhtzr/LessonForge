// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
using LessonForge.Core.Workflow;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace LessonForge.Core.Agents;

// Research Librarian — first node in the lesson crew.
//   ┌──────────────────┐
//   │ RESEARCH LIBRARIAN│ ──► Curriculum Designer ──► Assessment Writer ──► Accuracy Editor
//   └──────────────────┘
// Gathers accurate, grade-appropriate, citation-backed facts from Wikipedia and its fetchable
// cited references (the RAG index is built here and shared with the downstream stages).
public static partial class ResearchAgent
{
    public const string AgentName = "ResearchLibrarian";

    public const string AgentInstructions = """
        You are the Research Librarian for a school district. You gather accurate, grade-appropriate
        facts about a topic and keep every fact traceable to a real source.

        You are given a numbered list of SOURCE excerpts retrieved from Wikipedia and its cited
        references. Work ONLY from those excerpts — never add facts from your own memory, and never
        invent a source. If the excerpts do not cover something, leave it out.

        Produce:
        - topic_summary: a short, neutral, accurate overview of the topic.
        - grounded_facts: 8-15 concise, teachable facts. Each fact MUST include citations whose URLs
          come from the provided sources that actually support it.
        - queries_used: the aspects of the topic you focused on.

        Be factual and precise. Prefer foundational, widely-agreed facts over trivia. You trust
        encyclopedic and primary sources and cite everything.

        Output only valid JSON matching the ResearchOutput schema.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient)
        => chatClient.AsAIAgent(name: AgentName, instructions: AgentInstructions);

    public sealed partial class ResearchExecutor(AIAgent agent, RetrievalService retrieval, RagContext rag)
        : Executor("Research")
    {
        [MessageHandler]
        private async ValueTask<ResearchOutput> HandleAsync(
            LessonRequest request, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var state = await context.ReadStateAsync<LessonWorkflowState>(
                LessonStateShared.Key, scopeName: LessonStateShared.Scope) ?? new LessonWorkflowState();
            state.Request = request;
            state.CurrentStep = 1;

            // Build the per-run RAG index and share it with the downstream stages.
            var session = await retrieval.BuildAsync(request.Topic, progress: null, ct: cancellationToken);
            rag.Session = session;

            var queries = new[]
            {
                request.Topic,
                $"{request.Topic} overview key facts",
                $"{request.Topic} definition and importance",
                $"{request.Topic} examples and process"
            };
            var evidence = await RagPrompt.GatherEvidenceAsync(session, queries, ct: cancellationToken);

            var prompt = $"""
                Gather grounded facts about this topic for a lesson aimed at age {request.AudienceAge}.

                TOPIC: {request.Topic}

                SOURCES (use only these; cite by the URL shown):
                {evidence}

                Produce topic_summary, 8-15 grounded_facts each with citations drawn from the sources
                above, and queries_used. Output structured JSON.
                """;

            var response = await agent.RunAsync<ResearchOutput>(prompt, cancellationToken: cancellationToken);
            var output = response.Result;

            // Authoritative source inventory comes from the index, not the model.
            output.SourceInventory = RagPrompt.InventoryCitations(session);
            if (output.QueriesUsed.Count == 0)
                output.QueriesUsed = queries.ToList();

            state.Research = output;
            state.CurrentStep = 2;
            await context.QueueStateUpdateAsync(LessonStateShared.Key, state, scopeName: LessonStateShared.Scope);

            return output;
        }
    }
}
