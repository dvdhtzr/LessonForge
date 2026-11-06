// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
using LessonForge.Core.Workflow;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace LessonForge.Core.Agents;

/// <summary>
/// Optional MAF research executor: examines credible disagreement after factual research
/// and before the existing educator research approval gate.
/// </summary>
public static partial class PerspectiveAnalystAgent
{
    public const string AgentName = "PerspectiveAnalyst";

    public const string AgentInstructions = """
        You are an evidence-aware education researcher. Identify substantive, credible
        interpretive disagreements, not merely two opposing opinions. Distinguish
        documented historical perspectives from later scholarly interpretations and
        distinguish established facts from ethical/value disagreements.

        Examine ONLY the numbered SOURCE excerpts and the grounded facts in the prompt.
        Never invent references, quotations, evidence, participants, or opposing positions.
        A minority interpretation may be explained without treating it as equally supported.
        Do not create false balance around well-established evidence.

        Return a compact JSON object with EXACT keys:
        status: one of "supported_disagreement", "no_substantive_disagreement",
          "insufficient_evidence".
        perspectives: array of 2-3 objects ONLY for supported_disagreement; each has
          name, interpretation, evidence_basis, limitations and citations (array of
          one or more {title, url} objects copied from SOURCE excerpts).
          Use [] for the other statuses. Each interpretation is a meaningful
          perspective with evidence, not a rhetorical straw man.
        areas_of_agreement: array of concise agreed evidence-based points; [] is valid.
        evidence_limitations: short, nonempty assessment of limitations, including
          why some interpretations carry more or less evidence.
        discussion_questions: array of 1-3 age-appropriate evidence-evaluation prompts;
          [] is valid if no substantive debate can be established.

        If the supplied evidence supports no meaningful scholarly disagreement,
        status MUST be "no_substantive_disagreement" and perspectives MUST be [].
        If the evidence is too weak or narrow to decide, use "insufficient_evidence"
        with perspectives [] — never assume a debate exists.
        Use facts as facts, compare interpretations fairly, and keep the entire
        response short enough to complete its JSON output.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient, int maxOutputTokens)
        => StructuredAgentOutput.CreateSchemaAgent<PerspectiveAnalysisOutput>(
            chatClient, AgentName, AgentInstructions, maxOutputTokens);

    public sealed partial class PerspectiveAnalysisExecutor(
        AIAgent agent, RetrievalService retrieval, RagContext rag)
        : Executor("PerspectiveAnalysis")
    {
        [MessageHandler]
        private async ValueTask<ResearchOutput> HandleAsync(
            ResearchOutput research, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var state = await context.ReadStateAsync<LessonWorkflowState>(
                LessonStateShared.Key, scopeName: LessonStateShared.Scope)
                ?? throw new InvalidOperationException("Perspective Analyst cannot load workflow state.");

            var request = state.Request;
            if (!request.ExplorePerspectives)
                throw new InvalidOperationException(
                    "Perspective Analyst was invoked despite perspectives mode being disabled.");
            if (string.IsNullOrWhiteSpace(request.Topic) || research.GroundedFacts is not { Count: > 0 })
                throw new InvalidOperationException(
                    "Perspective Analyst requires the lesson topic and grounded research facts.");
            if (rag.Session is not { } originalSession)
                throw new InvalidOperationException(
                    "Perspective Analyst requires the existing source retrieval index.");

            // Extra discovery is optional: a narrowly phrased interpretive
            // search may find no eligible pages even when the primary topic
            // has ample legitimate sources. Never weaken the rights gate or
            // discard the existing evidence just to satisfy this second search.
            var discoveryQuery = $"{request.Topic} interpretations historical scholarly debates";
            var additionalSession = await retrieval.BuildSupplementaryAsync(
                discoveryQuery, ct: cancellationToken);
            rag.PerspectiveSession = additionalSession;

            var queries = new[]
            {
                request.Topic,
                $"{request.Topic} interpretations perspectives",
                $"{request.Topic} evidence historical scholarly debate"
            };
            var foundation = await RagPrompt.GatherEvidenceAsync(
                originalSession, queries, perQuery: 2, maxSnippets: 4,
                maxCharsPerSnippet: 500, ct: cancellationToken);
            var supplementary = additionalSession is not null
                ? await RagPrompt.GatherEvidenceAsync(
                    additionalSession, queries, perQuery: 2, maxSnippets: 4,
                    maxCharsPerSnippet: 500, ct: cancellationToken)
                : "(No additional rights-eligible perspective sources were retrieved. " +
                  "Evaluate only the original evidence; if it does not establish the " +
                  "interpretations, report insufficient_evidence, not an invented debate.)";

            var supplementaryCitations = additionalSession is not null
                ? RagPrompt.InventoryCitations(additionalSession)
                : [];
            var allowed = research.SourceInventory
                .Concat(supplementaryCitations)
                .Where(c => !string.IsNullOrWhiteSpace(c.Url))
                .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var facts = string.Join("\n", research.GroundedFacts.Take(10)
                .Select((fact, index) => $"{index + 1}. {fact.Statement}"));
            var prompt = $"""
                Topic: {request.Topic}
                Audience age: {request.AudienceAge}
                Teaching context: {request.AudienceNotes}

                APPROVED-TO-REVIEW GROUNDED FACTS:
                {facts}

                ORIGINAL RESEARCH SOURCE EXCERPTS:
                {foundation}

                ADDITIONAL PERSPECTIVE SOURCE EXCERPTS:
                {supplementary}

                Determine whether substantive, credible interpretive differences
                are supported by these sources. Do not manufacture sides or claim
                equal support where evidence is uneven. Only use citation URLs
                shown in the retrieved source excerpts. If sources do not support
                an interpretation, exclude it and state the limitation.
                Return the complete JSON object for PerspectiveAnalysisOutput.
                """;

            var result = await StructuredAgentOutput.RunAsync<PerspectiveAnalysisOutput>(
                agent, prompt, "PerspectiveAnalysis", cancellationToken);
            PerspectiveAnalysisValidator.Validate(result, allowed);

            // Attach the additional *retrieved* source inventory. A later educator
            // edit cannot cite material absent from the indexed source set.
            research.SourceInventory = allowed;
            research.Perspectives = result;
            state.Research = research;
            state.CurrentStep = 2;
            await context.QueueStateUpdateAsync(
                LessonStateShared.Key, state, scopeName: LessonStateShared.Scope);
            return research;
        }
    }
}
