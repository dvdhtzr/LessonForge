// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using System.Security.Cryptography;
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
        You are LessonForge's skeptical Accuracy Editor. Audit existing educator-approved
        content ONLY against supplied research and source excerpts. Never rewrite a lesson,
        quiz, answer key, or approval. No fabricated citations or certainty.

        Return a SMALL JSON object: flags, citation_coverage, notes.
        Each flag: id="" (host assigns it), claim (short focal concept),
        issue (why it matters), severity (REQUIRED on every flag), category, location, recommendation
        (specific educator action), evidence (brief source support or limitations;
        empty string if none). Use exact S#/Q# target IDs, or GENERAL.
        Category MUST be one of: factual_accuracy, missing_citation,
        unsupported_claim, misleading_wording, missing_prerequisite,
        instructional_gap, assessment_alignment, age_appropriateness,
        perspective_fairness, insufficient_evidence.

        Severity: High = material factual error, invalid assessment, or essential
        tested concept never taught (review before use); Medium = meaningful
        omission or qualification affecting comprehension/fairness; Low = minor
        wording/citation improvement; Informational = optional, not a defect.
        Severity is a judgment, not a confidence score. Every finding MUST
        include an explicit "severity" value from High, Medium, Low, or
        Informational. Never omit severity or infer a default for a finding.

        ONE finding per root issue. A quiz question testing an untaught concept
        warrants assessment_alignment at Q#, NOT duplicate section warnings.
        Don't fabricate opposing perspectives or mistake lack of a source excerpt
        for proof something was never taught: inputs are shortened.
        For each targeted quiz question, independently evaluate EVERY answer option
        against the stem and supplied evidence before checking the marked answer.
        If two or more options are defensible (including nested locations or
        equivalent wording), or none is correct, flag High assessment_alignment
        and name the competing option indices. Incorrect distractors are intentional:
        do NOT flag merely because they state something false.
        Flag malformed or unintelligible question stems as assessment_alignment;
        never equate a filled correct_index with an unambiguous question.
        Avoid repetition; prioritize substantive flaws; retain separate issues.
        Keep flags and explanations concise and fully finish JSON. Return [] when
        nothing is flagged. citation_coverage is an AI-estimated 0..1 fraction,
        not an audited metric. notes is one short sentence with limitations.
        """;

    public static AIAgent CreateAgent(IChatClient chatClient, int maxOutputTokens)
        => StructuredAgentOutput.CreateSchemaAgent<FactCheckReviewOutput>(
            chatClient, AgentName, AgentInstructions, maxOutputTokens);

    /// <summary>
    /// Single-target auditing never asks the model to supply a location:
    /// the workflow owns the S#/Q# identifier and binds it after validation.
    /// Other review fields remain compulsory and model-authored.
    /// </summary>
    public static AIAgent CreateSingleItemAgent(IChatClient chatClient, int maxOutputTokens)
        => StructuredAgentOutput.CreateSchemaAgent<SingleItemFactCheckOutput>(
            chatClient, "AccuracyEditorSingleItem", """
                You are LessonForge's skeptical Accuracy Editor. Audit only the
                educator-approved lesson section or quiz question identified
                in the prompt, against the provided, untrusted source excerpts.
                Do not rewrite the lesson or add facts not supported by evidence.

                Return one small JSON object with EXACT keys:
                flags (array, [] when no substantive findings),
                citation_coverage (number 0..1, AI estimate),
                notes (one short, nonempty sentence with limitations).

                Each flag MUST contain claim, issue, severity, category,
                recommendation, evidence. DO NOT include a location or ID:
                the application assigns the known review target itself.
                severity MUST be exactly High, Medium, Low or Informational.
                High: material factual/assessment error. Medium: meaningful
                omission affecting comprehension/fairness. Low: minor wording
                or citation improvement. Informational: optional suggestion.
                category MUST be one of: factual_accuracy, missing_citation,
                unsupported_claim, misleading_wording, missing_prerequisite,
                instructional_gap, assessment_alignment, age_appropriateness,
                perspective_fairness, insufficient_evidence.

                Every flag needs a short concrete claim, issue and specific
                educator action. Do not invent citations, certainty or debate.
                When the target is a quiz question, independently evaluate all
                options against the stem and supplied evidence before trusting
                the marked answer. If multiple choices are defensible (including
                overlapping locations or equivalent terms), or none is correct,
                flag High assessment_alignment and give their zero-based indices.
                An incorrect distractor is intentional, NOT a factual error to
                flag by itself. Flag malformed stems or unsupported answer keys.
                Keep the entire JSON compact and complete. It is valid to
                return flags=[] if there are no issues.
                """, maxOutputTokens);

    public sealed partial class FactCheckExecutor(
        AIAgent agent, AIAgent singleItemAgent, RagContext rag) : Executor("FactCheck")
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
            // source excerpts and complete JSON validation. Truncated or incomplete
            // two-item reviews are regenerated item by item. A one-item review
            // with missing required fields gets at most one full retry; persistent
            // failures stop the stage. No partial or invented audit results.
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

                if (scope.ItemCount == 1)
                {
                    // The model reports findings, NOT their location. The
                    // host assigns the only possible target after validating
                    // every required field. No retry can be exhausted by a
                    // malformed or missing location from the model.
                    var scoped = await StructuredAgentOutput.RunAsync<SingleItemFactCheckOutput>(
                        singleItemAgent, prompt, $"FactCheck {scope.Label}", token,
                        authoritativeSingleTarget: scope.Label);
                    return SingleItemFactCheckBoundary.AssignLocation(scoped, scope.Label);
                }

                return await StructuredAgentOutput.RunAsync<FactCheckReviewOutput>(
                    agent, prompt, $"FactCheck {scope.Label}", token);
            }, cancellationToken);

            // Deliberately do not trust the LLM to reconstruct or modify the approved
            // lesson, quiz, answers or citations. Only audit findings come from the LLM.
            var output = AssembleVerifiedLesson(plan, quiz, review, approvedPerspectives);
            // Persist the retrieved documents' provenance outside model-generated
            // DTOs. This is evidence inventory, not a declaration that every
            // document was quoted, cited or factually authoritative.
            var sourceSessions = new List<RetrievalSession> { session };
            if (rag.PerspectiveSession is { } perspectiveSession)
                sourceSessions.Add(perspectiveSession);
            output.SourceRecords = sourceSessions
                .SelectMany(s => s.Sources)
                .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(s => new SourceEvidenceRecord
                {
                    ProviderId = s.ProviderId,
                    CanonicalId = s.CanonicalId,
                    Title = s.Title,
                    Url = s.Url,
                    LicenseId = s.Rights?.LicenseId ?? "",
                    PolicyUrl = s.Rights?.PolicyUrl ?? "",
                    Attribution = s.Rights?.Attribution ?? "",
                    RightsScope = s.Rights?.Scope ?? "",
                    RightsReviewedOn = s.Rights?.ReviewedOn ?? default,
                    RetrievedAtUtc = s.RetrievedAtUtc,
                    DocumentSha256 = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(s.Text)))
                }).ToList();

            // An educator may have supplied duplicate links at either approval
            // gate. Do not change their lesson text or the verified findings,
            // but persist each distinct cited document only once per list.
            SourceCitationDeduplicator.Normalize(output);

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
        const int mapBudget = 1800;
        var usedMapChars = 0;
        foreach (var (section, index) in plan.Sections.Select((s, i) => (s, i)))
        {
            var line = $"S{index + 1}: {Short(section.Title, 60)} — {Short(section.Body, 125)}";
            if (usedMapChars + line.Length > mapBudget)
            {
                context.AppendLine("(additional lesson sections omitted from the map; do not infer absence)");
                break;
            }
            context.AppendLine(line);
            usedMapChars += line.Length;
        }

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
            for (var option = 0; option < Math.Min(q.Options.Count, 8); option++)
                context.AppendLine($"  {option}: {Short(q.Options[option], 100)}");
            if (q.Options.Count > 8)
                context.AppendLine("(remaining options omitted; cannot verify entire answer set)");
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

        var locationGuidance = scope.ItemCount == 1
            ? "Do not include location or ID in findings. The application knows the exact item ID " +
              "and assigns it after validating all substantive fields."
            : "For EVERY flag, include location exactly as one of the TARGET ITEM identifiers above, " +
              "or GENERAL for a truly global finding. Never omit it.";

        return $"""
            Review ONLY {scope.Label} from the approved lesson and quiz.
            Every target must be considered; do not return a review of other items.
            Keep the full JSON compact: at most TWO substantive findings per target,
            brief fields (claim < 55 characters, issue/recommendation < 150 each,
            evidence < 110). Prefer the issues most important to an educator.
            A missing concept in a shortened lesson map is NOT proof it was never taught.
            For EACH quiz target, first independently check the stem against ALL
            options and the evidence, without assuming the marked key is right.
            If more than one option can answer the question (including overlapping
            geography, synonyms, or a true subset of another true option), or
            none is correct, emit ONE High assessment_alignment finding naming the
            zero-based competing indices. A wrong distractor is intentional; do
            not flag the incorrect alternative merely for being false.
            Also flag malformed stems, answer-key mismatch, and genuinely unsupported
            claims. Do not replace quiz text or guess missing evidence.
            You may flag insufficient evidence, but do not assert certain absence
            unless the supplied material genuinely establishes it.

            {context}
            {perspectives}
            RETRIEVED EVIDENCE:
            {evidence}

            Return ONLY JSON with flags, citation_coverage, notes, using the required
            structured finding fields and severity rubric. EVERY flag MUST contain
            "severity" as exactly "High", "Medium", "Low", or "Informational",
            chosen using the rubric (do not omit or assume a default).
            {locationGuidance}
            Include empty flags if no issues found. Keep notes to one short sentence.
            No rewritten content.
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
