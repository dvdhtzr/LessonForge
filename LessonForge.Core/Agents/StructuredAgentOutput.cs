// Copyright (c) 2026 — LessonForge. Original work.

using System.Text.Json;
using LessonForge.Core.Dto;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LessonForge.Core.Agents;

/// <summary>
/// Local Ollama adapter for MAF agents that must return a validated JSON payload.
/// Request JSON-only final answers rather than relying on AgentResponse{T}.Result,
/// which can throw when a thinking model returns no final text.
/// </summary>
public static class StructuredAgentOutput
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Use the MAF chat-client agent, but explicitly request Ollama JSON mode and
    /// no separate reasoning channel for these schema-shaped tasks.
    /// </summary>
    public static AIAgent CreateAgent(IChatClient chat, string name, string instructions, int maxOutputTokens)
        => chat.AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                MaxOutputTokens = maxOutputTokens,
                ResponseFormat = ChatResponseFormat.Json,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None }
            }
        });

    /// <summary>
    /// Ask the provider to constrain the answer to the declared DTO shape rather than
    /// requesting merely syntactically valid JSON. Keep the normal agent pipeline.
    /// </summary>
    public static AIAgent CreateSchemaAgent<T>(IChatClient chat, string name, string instructions, int maxOutputTokens)
        => chat.AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                MaxOutputTokens = maxOutputTokens,
                ResponseFormat = ChatResponseFormat.ForJsonSchema<T>(s_json),
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None }
            }
        });

    public static async Task<T> RunAsync<T>(
        AIAgent agent, string prompt, string stage, CancellationToken ct = default,
        string? authoritativeSingleTarget = null,
        Action<string?>? inspectJsonForLocalEvaluation = null)
    {
        var response = await agent.RunAsync(prompt, cancellationToken: ct);
        // Explicitly opt-in only: permits a bounded structured diagnostic
        // preview before strict schema/field validation rejects the response.
        // Never log or persist the raw response here.
        inspectJsonForLocalEvaluation?.Invoke(response.Text);

        // Never put model text into diagnostics: source material is untrusted, may be large,
        // and could leak into logs or the browser's error message.
        var contentKinds = string.Join(", ", response.Messages
            .SelectMany(message => message.Contents)
            .Select(content => content.GetType().Name)
            .Distinct());

        return Parse<T>(response.Text, stage, response.FinishReason?.ToString(),
            contentKinds, authoritativeSingleTarget);
    }

    /// <summary>
    /// A pure, testable boundary: reject empty, invalid, and obviously incomplete
    /// outputs instead of silently passing empty DTOs to the next agent.
    /// </summary>
    public static T Parse<T>(
        string? text, string stage, string? finishReason = null, string? contentKinds = null,
        string? authoritativeSingleTarget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        // A model that reports "length" exhausted its generation budget. Even a
        // syntactically valid prefix must not be accepted as a complete lesson.
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{stage} output was truncated after {text?.Length ?? 0} characters " +
                "(finish reason: length). The generation hit a token limit. Check " +
                "LessonForge:Llm:MaxOutputTokens and Ollama's allocated context window " +
                "(`ollama ps`); incomplete JSON is never accepted.");

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"{stage} returned no final JSON answer (finish reason: {finishReason ?? "unknown"}; " +
                $"content types: {contentKinds ?? "unknown"}). " +
                "Check Ollama's model output and available context; reasoning-only output is not usable.");
        }

        T value;
        try
        {
            value = JsonSerializer.Deserialize<T>(text, s_json)
                ?? throw new JsonException("Deserialized JSON value was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{stage} returned malformed or incompatible JSON (finish reason: {finishReason ?? "unknown"}; " +
                $"response length: {text.Length} characters).", ex);
        }

        // Only the host knows which section/question is being audited.
        // In a single-item scope, its ID safely fills an omitted location;
        // other review fields, explicit locations, and multi-item audits
        // remain subject to the existing strict validation.
        if (value is FactCheckReviewOutput scopedReview &&
            authoritativeSingleTarget is not null)
            AccuracyReviewPolicy.RestoreMissingLocationsForSingleTarget(
                scopedReview, authoritativeSingleTarget);

        var missing = MissingRequiredContent(value);

        // Numeric DTO defaults hide an omitted correct_index (zero is also a valid
        // value), so inspect raw property presence for each question as well.
        if (value is QuizOutput quizOutput && quizOutput.Questions is { Count: > 0 })
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("questions", out var questionItems) &&
                questionItems.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in questionItems.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("correct_index", out _))
                        missing.Add($"questions[{index}].correct_index");
                    index++;
                }
            }
        }

        // The review DTO initializes flags to [] and coverage to zero; each
        // VerificationFlag also defaults Severity to "Medium". Those defaults
        // must NEVER turn omitted model fields into successful fact-checking.
        if (value is FactCheckReviewOutput or SingleItemFactCheckOutput)
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            foreach (var field in new[] { "flags", "citation_coverage", "notes" })
            {
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty(field, out _))
                {
                    if (!missing.Contains(field))
                        missing.Add(field);
                }
            }

            // For a one-item review, the host assigns the location. A model
            // need not output it at all. But if it explicitly names a DIFFERENT
            // canonical S#/Q# target (or GENERAL), reject rather than silently
            // attributing another item's finding to the host target.
            if (value is SingleItemFactCheckOutput &&
                authoritativeSingleTarget is not null &&
                root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("flags", out var scopedFlags) &&
                scopedFlags.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var flag in scopedFlags.EnumerateArray())
                {
                    if (flag.ValueKind == JsonValueKind.Object &&
                        flag.TryGetProperty("location", out var rawLocation) &&
                        rawLocation.ValueKind == JsonValueKind.String)
                    {
                        var location = rawLocation.GetString() ?? "";
                        var canonicalLocation = location == "GENERAL" ||
                            (location.Length >= 2 && (location[0] is 'S' or 'Q') &&
                             int.TryParse(location.AsSpan(1), out var num) && num > 0 &&
                             location == $"{location[0]}{num}");
                        if (canonicalLocation &&
                            !string.Equals(location, authoritativeSingleTarget, StringComparison.Ordinal))
                            missing.Add($"flags[{index}].location (outside {authoritativeSingleTarget})");
                    }
                    index++;
                }
            }

            // Explicit severity is required on every finding. Only the model
            // can apply the educator-facing rubric; the host must not silently
            // use the DTO's "Medium" default for a missing JSON property.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("flags", out var flags) &&
                flags.ValueKind == JsonValueKind.Array)
            {
                var flagIndex = 0;
                foreach (var flag in flags.EnumerateArray())
                {
                    if (flag.ValueKind == JsonValueKind.Object &&
                        !flag.TryGetProperty("severity", out _))
                    {
                        var path = $"flags[{flagIndex}].severity";
                        if (!missing.Contains(path))
                            missing.Add(path);
                    }
                    flagIndex++;
                }
            }
        }

        if (missing.Count > 0)
        {
            // With System.Text.Json's default-initialized DTO properties, both a missing
            // array and a supplied empty array look identical after deserialization.
            // Distinguish them without exposing generated text or source material.
            var requiredArray = value switch
            {
                ResearchOutput when missing.Contains("grounded_facts") => "grounded_facts",
                ResearchDraftOutput when missing.Contains("grounded_facts") => "grounded_facts",
                QuizOutput when missing.Contains("questions") => "questions",
                _ => null
            };
            if (requiredArray is not null)
            {
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                string detail;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty(requiredArray, out var array))
                {
                    detail = "property absent";
                }
                else if (array.ValueKind == JsonValueKind.Array)
                {
                    detail = array.GetArrayLength() == 0 ? "empty array" : "invalid entries";
                }
                else
                {
                    detail = "not an array";
                }

                missing[missing.IndexOf(requiredArray)] = $"{requiredArray} ({detail})";
            }

            var kind = value switch
            {
                FactCheckReviewOutput or SingleItemFactCheckOutput => "review fields",
                ResearchSummaryRepairOutput => "research summary",
                _ => "lesson content or citation arrays"
            };
            throw new InvalidOperationException(
                $"{stage} returned valid JSON but omitted required {kind}. " +
                $"Missing or invalid fields: {string.Join(", ", missing)}. " +
                "The result was rejected rather than treating an empty payload as successful.");
        }

        return value;
    }

    // Report only known DTO field paths; never echo generated model text into logs.
    // Validation is intentionally deterministic and independent of the model provider.
    private static List<string> MissingRequiredContent<T>(T value)
    {
        var missing = new List<string>();
        switch (value)
        {
            case ResearchDraftOutput draft:
                missing.AddRange(MissingRequiredContent(draft.ToResearchOutput()));
                break;

            case ResearchSummaryRepairOutput repair:
                if (string.IsNullOrWhiteSpace(repair.TopicSummary))
                    missing.Add("topic_summary");
                break;

            case ResearchOutput research:
                if (string.IsNullOrWhiteSpace(research.TopicSummary)) missing.Add("topic_summary");
                if (research.GroundedFacts is not { Count: > 0 })
                    missing.Add("grounded_facts");
                else
                {
                    foreach (var (fact, index) in research.GroundedFacts.Select((fact, index) => (fact, index)))
                    {
                        if (fact is null || string.IsNullOrWhiteSpace(fact.Statement))
                            missing.Add($"grounded_facts[{index}].statement");
                        if (fact?.Citations is not { Count: > 0 } ||
                            fact.Citations.Any(c => c is null ||
                                string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Url)))
                            missing.Add($"grounded_facts[{index}].citations");
                    }
                }
                break;

            case LessonPlanOutput plan:
                if (string.IsNullOrWhiteSpace(plan.Title)) missing.Add("title");
                if (string.IsNullOrWhiteSpace(plan.Overview)) missing.Add("overview");
                if (plan.LearningObjectives is not { Count: > 0 } ||
                    plan.LearningObjectives.Any(string.IsNullOrWhiteSpace))
                    missing.Add("learning_objectives");
                if (plan.Sections is not { Count: > 0 })
                    missing.Add("sections");
                else
                {
                    foreach (var (section, index) in plan.Sections.Select((section, index) => (section, index)))
                    {
                        if (section is null || string.IsNullOrWhiteSpace(section.Title))
                            missing.Add($"sections[{index}].title");
                        if (section is null || string.IsNullOrWhiteSpace(section.Body))
                            missing.Add($"sections[{index}].body");
                    }
                }
                break;

            case QuizOutput quiz:
                if (quiz.Questions is not { Count: > 0 })
                {
                    missing.Add("questions");
                }
                else
                {
                    for (var index = 0; index < quiz.Questions.Count; index++)
                    {
                        var question = quiz.Questions[index];
                        if (question is null)
                        {
                            missing.Add($"questions[{index}]");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(question.Stem))
                            missing.Add($"questions[{index}].stem");
                        if (question.Options is not { Count: >= 2 } ||
                            question.Options.Any(string.IsNullOrWhiteSpace))
                            missing.Add($"questions[{index}].options");
                        if (question.Options is { Count: > 0 } &&
                            (question.CorrectIndex < 0 || question.CorrectIndex >= question.Options.Count))
                            missing.Add($"questions[{index}].correct_index");
                        if (string.IsNullOrWhiteSpace(question.Explanation))
                            missing.Add($"questions[{index}].explanation");
                        if (question.Citations is not { Count: > 0 } ||
                            question.Citations.Any(c => c is null ||
                                string.IsNullOrWhiteSpace(c.Title) ||
                                string.IsNullOrWhiteSpace(c.Url)))
                            missing.Add($"questions[{index}].citations");
                    }
                }
                foreach (var issue in QuizAnswerOptionPolicy.InvalidFields(quiz))
                    if (!missing.Contains(issue, StringComparer.Ordinal))
                        missing.Add(issue);
                break;

            case AnswerUniquenessReview uniqueness:
                missing.AddRange(AnswerUniquenessPolicy.InvalidFields(uniqueness));
                break;

            case FactCheckReviewOutput review:
                missing.AddRange(AccuracyReviewPolicy.InvalidFields(review));
                break;

            case SingleItemFactCheckOutput singleItem:
                missing.AddRange(SingleItemFactCheckBoundary.InvalidFields(singleItem));
                break;

            case VerifiedLessonOutput verified:
                if (verified.Plan?.Sections is not { Count: > 0 }) missing.Add("plan.sections");
                if (verified.Quiz?.Questions is not { Count: > 0 }) missing.Add("quiz.questions");
                break;
        }
        return missing;
    }
}
