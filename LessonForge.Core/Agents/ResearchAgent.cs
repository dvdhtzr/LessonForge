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
        - grounded_facts: 8-10 concise, one-sentence teachable facts. Each fact MUST have a
          "citations" (plural) ARRAY of 1-2 objects with "title" and "url", copied from
          the supplied supporting SOURCE excerpts. Never use a singular "citation" field.
        - queries_used: an empty array (the application supplies its actual queries).
        - source_inventory: an empty array (the application supplies its verified source inventory).
        Keep topic_summary to 1-2 sentences. Minimize verbosity; complete the entire JSON
        object before stopping.

        Be factual and precise. Prefer foundational, widely-agreed facts over trivia. You trust
        encyclopedic and primary sources and cite everything.

        Use the exact JSON field names topic_summary, grounded_facts, statement,
        citations, title, url, queries_used, and source_inventory. Each grounded_facts
        entry is an object with a nonempty statement and a nonempty citations array.
        Do not wrap the results inside a "research" or "lesson" object.
        Do not produce interpretive perspectives; a separate agent handles them
        only when the educator enables that feature. Do not include a perspectives field.
        Output only valid JSON matching the ResearchDraftOutput schema.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient, int maxOutputTokens)
        => StructuredAgentOutput.CreateSchemaAgent<ResearchDraftOutput>(
            chatClient, AgentName, AgentInstructions, maxOutputTokens);

    public sealed partial class ResearchExecutor(
        AIAgent agent, RetrievalService retrieval, RagContext rag, IProgress<string>? progress = null)
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
            var session = await retrieval.BuildAsync(request.Topic, progress, ct: cancellationToken);
            rag.Session = session;

            var queries = new[]
            {
                request.Topic,
                $"{request.Topic} overview key facts",
                $"{request.Topic} definition and importance",
                $"{request.Topic} examples and process"
            };
            progress?.Report("Searching the indexed sources for grounded facts…");
            // Keep the research prompt bounded for Ollama's common 4K local context.
            // Long-form research should use a larger configured model context or a
            // multi-pass evidence synthesis step rather than silently stuffing more chunks.
            var evidence = await RagPrompt.GatherEvidenceAsync(
                session, queries, perQuery: 3, maxSnippets: 8, maxCharsPerSnippet: 750,
                ct: cancellationToken);

            var prompt = $"""
                Gather grounded facts about this topic for a lesson aimed at age {request.AudienceAge}.

                TOPIC: {request.Topic}

                SOURCES (use only these; cite by the URL shown):
                {evidence}

                Produce 8-10 concise grounded_facts. Return only a single complete JSON object,
                with exactly these top-level keys: topic_summary, grounded_facts, queries_used,
                source_inventory. Each grounded_facts item must have "statement" and "citations";
                "citations" must be an array of objects with "title" and "url" from the
                supporting SOURCE excerpts. Set queries_used and source_inventory to [] because
                the application populates these fields. Do not use "citation" (singular).
                Use a one- or two-sentence topic_summary. No markdown or extra commentary.
                """;

            progress?.Report("Asking the local model to assemble the research report…");
            var draft = await StructuredAgentOutput.RunAsync<ResearchDraftOutput>(
                agent, prompt, "Research", cancellationToken);
            var output = draft.ToResearchOutput();
            progress?.Report(request.ExplorePerspectives
                ? "Research report generated; passing evidence to Perspective Analyst."
                : "Research report generated; preparing educator review.");

            // Authoritative source inventory comes from the index, not the model.
            // The optional Perspective Analyst owns this field. Never accept speculative
            // perspectives from the factual Research Librarian stage.
            output.Perspectives = null;
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
