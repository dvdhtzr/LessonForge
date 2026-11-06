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
    public static AIAgent CreateAgent(IChatClient chat, string name, string instructions)
        => chat.AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                ResponseFormat = ChatResponseFormat.Json,
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

        var invalid = value switch
        {
            ResearchOutput research =>
                string.IsNullOrWhiteSpace(research.TopicSummary) || research.GroundedFacts is not { Count: > 0 },
            LessonPlanOutput plan =>
                string.IsNullOrWhiteSpace(plan.Title) || plan.Sections is not { Count: > 0 },
            QuizOutput quiz =>
                quiz.Questions is not { Count: > 0 },
            VerifiedLessonOutput verified =>
                verified.Plan?.Sections is not { Count: > 0 } ||
                verified.Quiz?.Questions is not { Count: > 0 },
            _ => false
        };

        if (invalid)
            throw new InvalidOperationException(
                $"{stage} returned valid JSON but omitted required lesson content. " +
                "The result was rejected rather than treating an empty payload as successful.");

        return value;
    }
}
