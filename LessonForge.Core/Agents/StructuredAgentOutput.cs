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
        AIAgent agent, string prompt, string stage, CancellationToken ct = default)
    {
        var response = await agent.RunAsync(prompt, cancellationToken: ct);

        // Never put model text into diagnostics: source material is untrusted, may be large,
        // and could leak into logs or the browser's error message.
        var contentKinds = string.Join(", ", response.Messages
            .SelectMany(message => message.Contents)
            .Select(content => content.GetType().Name)
            .Distinct());

        return Parse<T>(response.Text, stage, response.FinishReason?.ToString(), contentKinds);
    }

    /// <summary>
    /// A pure, testable boundary: reject empty, invalid, and obviously incomplete
    /// outputs instead of silently passing empty DTOs to the next agent.
    /// </summary>
    public static T Parse<T>(
        string? text, string stage, string? finishReason = null, string? contentKinds = null)
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

        var missing = MissingRequiredContent(value);
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"{stage} returned valid JSON but omitted required lesson content or citation arrays. " +
                $"Missing or invalid fields: {string.Join(", ", missing)}. " +
                "The result was rejected rather than treating an empty payload as successful.");

        return value;
    }

    // Report only known DTO field paths; never echo generated model text into logs.
    // Validation is intentionally deterministic and independent of the model provider.
    private static List<string> MissingRequiredContent<T>(T value)
    {
        var missing = new List<string>();
        switch (value)
        {
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
                if (quiz.Questions is not { Count: > 0 }) missing.Add("questions");
                break;

            case VerifiedLessonOutput verified:
                if (verified.Plan?.Sections is not { Count: > 0 }) missing.Add("plan.sections");
                if (verified.Quiz?.Questions is not { Count: > 0 }) missing.Add("quiz.questions");
                break;
        }
        return missing;
    }

        return value;
    }
}
