// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
using LessonForge.Core.Workflow;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace LessonForge.Core.Agents;

// Accuracy Editor — final MAF executor. It reviews the already-approved lesson and
// completed quiz but does NOT regenerate either. Only its compact findings are
// model-generated. The host deterministically assembles the final persisted result.
public static partial class FactCheckAgent
{
    public const string AgentName = "AccuracyEditor";

    public const string AgentInstructions = """
        You are a skeptical educational fact-checker. Verify existing lesson and quiz
        claims against the supplied retrieved source excerpts and approved research.
        Do not use general model memory to invent facts or references.

        You are NOT the lesson author. Do not return a rewritten plan or quiz and do not
        attempt to change answers, activities, citations or lesson text. Instead, flag
        incorrect, unsupported, unverifiable or misleading claims for educator review.
        Identify each issue by its original claim or numbered item (S1, Q1, etc.).

        Output only a SMALL JSON object with exactly these top-level keys:
        - "flags": an array of objects, each with nonempty "claim", "issue", and
          "severity" (High, Medium, or Low). Return [] if nothing was flagged.
        - "citation_coverage": a number from 0 to 1 estimating the fraction of
          reviewed claims supported by supplied evidence. Do not invent precision.
        - "notes": 1-3 sentences describing your review and any evidence limitations.

        Review each lesson section and quiz answer for source support; uncertainty is a
        reason to flag a claim, not to assert it is true. Keep flags and notes concise,
        finish the entire JSON object, and output no markdown or other keys.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient, int maxOutputTokens)
        => StructuredAgentOutput.CreateSchemaAgent<FactCheckReviewOutput>(
            chatClient, AgentName, AgentInstructions, maxOutputTokens);

    public sealed partial class FactCheckExecutor(AIAgent agent, RagContext rag) : Executor("FactCheck")
    {
        [MessageHandler]
        private async ValueTask<VerifiedLessonOutput> HandleAsync(
            QuizOutput quiz, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var state = await context.ReadStateAsync<LessonWorkflowState>(
                LessonStateShared.Key, scopeName: LessonStateShared.Scope)
                ?? throw new InvalidOperationException("Accuracy Editor cannot load workflow state.");

            var plan = state.Plan
                ?? throw new InvalidOperationException("Accuracy Editor received no approved lesson plan.");
            if (plan.Sections is not { Count: > 0 } || quiz.Questions is not { Count: > 0 })
                throw new InvalidOperationException(
                    "Accuracy Editor requires the approved lesson sections and generated quiz questions.");

            if (rag.Session is not { } session)
                throw new InvalidOperationException(
                    "Accuracy Editor cannot verify claims because the source retrieval index is missing.");

            // Bound input context for local Ollama. The old implementation sent long
            // sections, every quiz question, up to 20 large RAG snippets, and asked the
            // model to regenerate all of that as a single JSON output.
            var queries = plan.LearningObjectives
                .Concat(quiz.Questions.Select(q => q.Stem))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(12);

            var evidence = await RagPrompt.GatherEvidenceAsync(
                session, queries, perQuery: 2, maxSnippets: 8, maxCharsPerSnippet: 600,
                ct: cancellationToken);
            if (evidence == "(no sources retrieved)")
                throw new InvalidOperationException(
                    "Accuracy Editor cannot verify claims because no source excerpts were retrieved.");

            var approvedPerspectives = state.Research?.Perspectives;
            if (state.Request.ExplorePerspectives && approvedPerspectives is null)
                throw new InvalidOperationException(
                    "Accuracy Editor is missing the educator-approved perspective analysis.");
            if (approvedPerspectives is not null)
            {
                PerspectiveAnalysisValidator.Validate(
                    approvedPerspectives, state.Research!.SourceInventory);
                if (rag.PerspectiveSession is { } additional)
                {
                    var perspectiveQueries = approvedPerspectives.Perspectives
                        .Select(p => p.Name)
                        .Prepend(state.Request.Topic)
                        .Take(4);
                    var additionalEvidence = await RagPrompt.GatherEvidenceAsync(
                        additional, perspectiveQueries, perQuery: 2, maxSnippets: 3,
                        maxCharsPerSnippet: 450, ct: cancellationToken);
                    if (additionalEvidence != "(no sources retrieved)")
                        evidence += "\n" + additionalEvidence;
                }
            }

            var prompt = BuildReviewPrompt(plan, quiz, state.Research, evidence);
            var review = await StructuredAgentOutput.RunAsync<FactCheckReviewOutput>(
                agent, prompt, "FactCheck", cancellationToken);

            // Deliberately do not trust the LLM to reconstruct or modify the approved
            // lesson, quiz, answers or citations. Only audit findings come from the LLM.
            var output = AssembleVerifiedLesson(plan, quiz, review, approvedPerspectives);

            state.CurrentStep = 5;
            await context.QueueStateUpdateAsync(LessonStateShared.Key, state, scopeName: LessonStateShared.Scope);
            return output;
        }
    }

    /// <summary>Creates a bounded, read-only review request, never a reproduction task.</summary>
    public static string BuildReviewPrompt(
        LessonPlanOutput plan, QuizOutput quiz, ResearchOutput? research, string evidence)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(quiz);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);

        static string Trim(string? text, int maxChars)
            => text is null ? "" : text.Length <= maxChars ? text : text[..maxChars] + "…";

        var context = new StringBuilder();
        context.AppendLine($"LESSON: {Trim(plan.Title, 160)}");
        context.AppendLine("LEARNING OBJECTIVES:");
        foreach (var objective in plan.LearningObjectives.Take(8))
            context.AppendLine($"- {Trim(objective, 220)}");

        context.AppendLine("APPROVED RESEARCH FACTS:");
        if (research is { GroundedFacts.Count: > 0 })
            foreach (var fact in research.GroundedFacts.Take(10))
                context.AppendLine($"- {Trim(fact.Statement, 240)}");

        context.AppendLine("LESSON SECTIONS:");
        for (var i = 0; i < plan.Sections.Count; i++)
        {
            var section = plan.Sections[i];
            context.AppendLine($"S{i + 1}: {Trim(section.Title, 140)} — {Trim(section.Body, 550)}");
        }

        context.AppendLine("QUIZ ANSWERS TO VERIFY:");
        for (var i = 0; i < quiz.Questions.Count; i++)
        {
            var q = quiz.Questions[i];
            var answer = q.CorrectIndex >= 0 && q.CorrectIndex < q.Options.Count
                ? q.Options[q.CorrectIndex] : "(invalid index)";
            context.AppendLine(
                $"Q{i + 1}: {Trim(q.Stem, 220)} | answer: {Trim(answer, 120)} | " +
                $"explanation: {Trim(q.Explanation, 260)}");
        }

        var perspectiveContext = PerspectivePrompt.ForLesson(research?.Perspectives);
        var perspectiveReview = research?.Perspectives is not null
            ? "Check that differing perspectives are represented fairly in the lesson, supported by the cited evidence and weighted appropriately. Flag any invented, omitted or misleading interpretation. Do not insist on artificial balance."
            : "";

        return $"""
            Review the EXISTING educator-approved lesson and quiz. Do not rewrite either.

            MATERIAL TO CHECK:
            {context}

            {perspectiveContext}
            {perspectiveReview}

            RETRIEVED SOURCE EVIDENCE (the only independent evidence for verification):
            {evidence}

            Return only a small JSON object with "flags", "citation_coverage", "notes".
            Flags should identify original S# or Q# items and explain unsupported
            claims. An empty flags array is acceptable only if no issues were found.
            Never return "plan", "quiz", rewritten content or source excerpts.
            """;
    }

    /// <summary>
    /// Keep the educator-approved artifacts intact. The model only supplies findings;
    /// it cannot change lesson sections, the answer key or their citations.
    /// </summary>
    public static VerifiedLessonOutput AssembleVerifiedLesson(
        LessonPlanOutput approvedPlan, QuizOutput quiz, FactCheckReviewOutput review,
        PerspectiveAnalysisOutput? perspectives = null)
    {
        ArgumentNullException.ThrowIfNull(approvedPlan);
        ArgumentNullException.ThrowIfNull(quiz);
        ArgumentNullException.ThrowIfNull(review);

        if (approvedPlan.Sections is not { Count: > 0 } || quiz.Questions is not { Count: > 0 })
            throw new InvalidOperationException("Cannot finalize a lesson with empty plan or quiz content.");

        if (review.Flags is null || string.IsNullOrWhiteSpace(review.Notes) ||
            double.IsNaN(review.CitationCoverage) || double.IsInfinity(review.CitationCoverage) ||
            review.CitationCoverage is < 0 or > 1 ||
            review.Flags.Any(flag => flag is null ||
                string.IsNullOrWhiteSpace(flag.Claim) ||
                string.IsNullOrWhiteSpace(flag.Issue) ||
                flag.Severity is not ("High" or "Medium" or "Low")))
            throw new InvalidOperationException("Accuracy Editor returned incomplete or invalid review findings.");

        return new VerifiedLessonOutput
        {
            Plan = approvedPlan,
            Quiz = quiz,
            Perspectives = perspectives,
            Flags = review.Flags,
            CitationCoverage = review.CitationCoverage,
            Notes = review.Notes
        };
    }
}
