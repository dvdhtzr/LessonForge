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
        You are a skeptical educational Accuracy Editor. Audit an EXISTING educator-approved
        lesson and quiz against supplied retrieved evidence and approved research.
        Do NOT rewrite the plan, quiz, answers, or educator edits. Do not invent facts,
        citations, URLs, certainty, or artificial historical/scientific controversy.

        Return ONE small JSON object with exactly "flags", "citation_coverage", "notes".
        Each flag has these properties:
        - "id": "" (the host assigns its stable identifier).
        - "claim": SHORT focal concept or specific claim, e.g. "visa running".
          Use the same concise claim for warnings about the SAME root issue.
        - "issue": concise explanation of why this is a problem for learners.
        - "severity": exactly High, Medium, Low, or Informational.
        - "category": exactly one of factual_accuracy, missing_citation,
          unsupported_claim, misleading_wording, missing_prerequisite,
          instructional_gap, assessment_alignment, age_appropriateness,
          perspective_fairness, insufficient_evidence.
        - "location": exactly S1/S2/etc for section, Q1/Q2/etc for question,
          or GENERAL when no single item applies. Use supplied identifiers.
        - "recommendation": one concrete, bounded educator action for this issue.
        - "evidence": a short characterization of supplied source evidence or
          its absence; leave "" when unavailable. Never invent a citation.

        SEVERITY RUBRIC:
        High: material factual error, seriously misleading teaching, invalid quiz
        answer, or quiz testing an essential concept never introduced; educator
        must review before classroom use.
        Medium: meaningful omission, qualification, prerequisite or fairness gap
        that may impair comprehension.
        Low: minor wording/presentation issue or citation improvement where the
        underlying content is otherwise supported.
        Informational: optional enhancement, NOT a defect.
        Do not elevate solely to sound cautious. Severity is a judgment, not
        a statistical confidence measure.

        CATEGORY RULES:
        If a quiz asks about a concept the lesson never teaches, issue ONE
        assessment_alignment finding at Q#, not separate S# and Q# warnings.
        Use instructional_gap for teaching omissions not tied to an assessment;
        missing_citation for traceability, unsupported_claim for evidence deficits,
        and perspective_fairness only where credible interpretations are distorted.

        One finding per root cause. Do not repeat an issue under multiple categories
        or severities. Flag distinct problems separately even when in the same item.
        The lesson/source excerpts may be truncated: absence from a shortened excerpt
        alone does NOT prove a term was never taught. Explain that limitation instead
        of claiming certain absence. Prioritize substantive defects. Return [] when none found.
        "citation_coverage" is an AI-ESTIMATED 0..1 fraction of reviewed claims
        supported by supplied evidence, NOT an audited metric. Do not invent precision.
        "notes" should give 1-3 neutral sentences including evidence limitations.
        Output complete JSON only, with no markdown, no extra top-level keys.
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

            var approvedPerspectives = state.Research?.Perspectives;
            if (state.Request.ExplorePerspectives && approvedPerspectives is null)
                throw new InvalidOperationException(
                    "Accuracy Editor is missing the educator-approved perspective analysis.");
            if (approvedPerspectives is not null)
            {
                var verifiedSources = RagPrompt.InventoryCitations(session);
                if (rag.PerspectiveSession is { } indexedPerspectives)
                    verifiedSources.AddRange(RagPrompt.InventoryCitations(indexedPerspectives));
                PerspectiveAnalysisValidator.Validate(approvedPerspectives, verifiedSources);
            }

            // Check ALL sections and questions, two at a time. Every batch gets its own
            // relevant source excerpts and complete JSON validation. If a two-item
            // batch hits the model's output limit, retry its items separately.
            // A single-item failure still fails the stage; there is no partial audit.
            var review = await AccuracyReviewBatcher.RunAsync(plan, quiz, async (scope, token) =>
            {
                var queries = scope.Sections
                    .Select(i => plan.Sections[i].Title + " " + plan.Sections[i].Body[..Math.Min(160, plan.Sections[i].Body.Length)])
                    .Concat(scope.Questions.Select(i => quiz.Questions[i].Stem))
                    .Append(state.Request.Topic)
                    .Where(s => !string.IsNullOrWhiteSpace(s));

                var evidence = await RagPrompt.GatherEvidenceAsync(
                    session, queries, perQuery: 1, maxSnippets: 4,
                    maxCharsPerSnippet: 480, ct: token);
                if (evidence == "(no sources retrieved)")
                    throw new InvalidOperationException(
                        $"Accuracy Editor cannot verify {scope.Label} because no source excerpts were retrieved.");

                if (rag.PerspectiveSession is { } perspectiveSession && approvedPerspectives is not null)
                {
                    var extra = await RagPrompt.GatherEvidenceAsync(
                        perspectiveSession, queries, perQuery: 1, maxSnippets: 2,
                        maxCharsPerSnippet: 360, ct: token);
                    if (extra != "(no sources retrieved)")
                        evidence += "\n" + extra;
                }

                var prompt = BuildScopedReviewPrompt(
                    plan, quiz, state.Research, evidence, scope, state.Request.AudienceAge);

                return await StructuredAgentOutput.RunAsync<FactCheckReviewOutput>(
                    agent, prompt, $"FactCheck {scope.Label}", token);
            }, cancellationToken);

            // Deliberately do not trust the LLM to reconstruct or modify the approved
            // lesson, quiz, answers or citations. Only audit findings come from the LLM.
            var output = AssembleVerifiedLesson(plan, quiz, review, approvedPerspectives);

            state.CurrentStep = 5;
            await context.QueueStateUpdateAsync(LessonStateShared.Key, state, scopeName: LessonStateShared.Scope);
            return output;
        }
    }

    /// <summary>
    /// Compact prompt for one or two target items. The full plan is available as
    /// a SHORT map for quiz/lesson alignment, while the exact target text gets
    /// more detail. Truncation is disclosed instead of treating missing context
    /// as proof that a concept was never taught.
    /// </summary>
    public static string BuildScopedReviewPrompt(
        LessonPlanOutput plan, QuizOutput quiz, ResearchOutput? research,
        string evidence, AccuracyReviewBatcher.Scope scope, int audienceAge)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(quiz);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);

        static string Short(string? input, int max)
            => string.IsNullOrWhiteSpace(input) ? "" :
                input.Length <= max ? input : input[..max] + "…";

        var context = new StringBuilder();
        context.AppendLine($"LESSON: {Short(plan.Title, 130)}");
        context.AppendLine($"AGE: {audienceAge}; GRADE: {Short(plan.GradeBand, 70)}");
        context.AppendLine($"OBJECTIVES: {Short(string.Join("; ", plan.LearningObjectives.Take(6)), 480)}");
        context.AppendLine("LESSON MAP (excerpts may omit material; do NOT infer absence from them):");
        foreach (var (section, index) in plan.Sections.Select((s, i) => (s, i)))
            context.AppendLine($"S{index + 1}: {Short(section.Title, 60)} — {Short(section.Body, 125)}");

        if (plan.Vocabulary.Count > 0)
            context.AppendLine("TAUGHT VOCABULARY: " +
                Short(string.Join("; ", plan.Vocabulary.Take(12).Select(v =>
                    $"{Short(v.Term, 45)}: {Short(v.Definition, 65)}")), 850));

        context.AppendLine("TARGET ITEMS (audit ONLY these identifiers):");
        foreach (var i in scope.Sections)
        {
            var section = plan.Sections[i];
            context.AppendLine($"S{i + 1}: {Short(section.Title, 110)} — {Short(section.Body, 650)}");
        }
        foreach (var i in scope.Questions)
        {
            var q = quiz.Questions[i];
            context.AppendLine($"Q{i + 1}: {Short(q.Stem, 250)}");
            for (var option = 0; option < q.Options.Count; option++)
                context.AppendLine($"  {option}: {Short(q.Options[option], 100)}");
            context.AppendLine($"  Correct index: {q.CorrectIndex}; explanation: {Short(q.Explanation, 310)}");
        }

        if (research is { GroundedFacts.Count: > 0 })
        {
            context.AppendLine("RESEARCH FACTS (bounded excerpts):");
            foreach (var fact in research.GroundedFacts.Take(6))
                context.AppendLine("- " + Short(fact.Statement, 150));
        }

        var perspectives = research?.Perspectives is null ? ""
            : Short(PerspectivePrompt.ForLesson(research.Perspectives), 1300) +
              "\nCheck fairness but never manufacture opposing sides.";

        return $"""
            Review ONLY {scope.Label} from the approved lesson and quiz.
            Every target must be considered; do not return a review of other items.
            Keep the full JSON compact: at most TWO substantive findings per target,
            brief fields (claim < 55 characters, issue/recommendation < 150 each,
            evidence < 110). Prefer the issues most important to an educator.
            A missing concept in a shortened lesson map is NOT proof it was never taught.
            You may flag insufficient evidence, but do not assert certain absence
            unless the supplied material genuinely establishes it.

            {context}
            {perspectives}
            RETRIEVED EVIDENCE:
            {evidence}

            Return ONLY JSON with flags, citation_coverage, notes, using the required
            structured finding fields and severity rubric. Include empty flags if
            no issues found. Keep notes to one short sentence. No rewritten content.
            """;
    }

    /// <summary>Creates a bounded, read-only review request, never a reproduction task.</summary>
    public static string BuildReviewPrompt(
        LessonPlanOutput plan, QuizOutput quiz, ResearchOutput? research, string evidence, int audienceAge = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(quiz);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);

        static string Trim(string? text, int maxChars)
            => text is null ? "" : text.Length <= maxChars ? text : text[..maxChars] + "…";

        var context = new StringBuilder();
        context.AppendLine($"LESSON: {Trim(plan.Title, 160)}");
        context.AppendLine($"GRADE BAND: {Trim(plan.GradeBand, 90)}; AUDIENCE AGE: {audienceAge}");
        context.AppendLine("LEARNING OBJECTIVES:");
        foreach (var objective in plan.LearningObjectives.Take(8))
            context.AppendLine($"- {Trim(objective, 220)}");

        context.AppendLine("TERMS ALREADY TAUGHT:");
        foreach (var term in plan.Vocabulary.Take(12))
            context.AppendLine($"- {Trim(term.Term, 65)}: {Trim(term.Definition, 110)}");

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
            Identify the affected S# or Q# exactly. Audit whether Q# tests concepts
            introduced in the plan. Consolidate missing-taught-concept warnings into
            one assessment_alignment finding at Q#, rather than duplicate warnings.
            Apply the severity rubric and give specific educator recommendations.
            No invented citations, repeated issues, or rewritten content.
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

        // Validate against the actual approved plan and quiz. IDs and conservative
        // duplicate grouping are host-owned, never model-generated.
        var findings = AccuracyReviewPolicy.Prepare(review, approvedPlan, quiz);

        return new VerifiedLessonOutput
        {
            Plan = approvedPlan,
            Quiz = quiz,
            Perspectives = perspectives,
            Flags = findings,
            CitationCoverage = review.CitationCoverage,
            Notes = review.Notes
        };
    }
}
