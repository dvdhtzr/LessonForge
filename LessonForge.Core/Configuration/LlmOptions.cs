// Copyright (c) 2026 — LessonForge. Original work.

namespace LessonForge.Core.Configuration;

/// <summary>
/// Local-LLM connection settings for LessonForge, bound from the <c>LessonForge:Llm</c>
/// section of <c>appsettings.json</c> (or overridden via environment variables like
/// <c>LessonForge__Llm__ChatModel</c>).
///
/// Everything runs against a local <b>Ollama</b> server through its OpenAI-compatible API —
/// no cloud keys, no external endpoints. Defaults target a stock Ollama install.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "LessonForge:Llm";

    /// <summary>Ollama's OpenAI-compatible endpoint. Must include the <c>/v1</c> suffix.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434/v1";

    /// <summary>
    /// The primary reasoning/generation model (lesson planning, quiz authoring, fact-checking).
    /// Pull it first with: <c>ollama pull qwen3.8:27b</c>.
    /// </summary>
    public string ChatModel { get; set; } = "qwen3.8:27b";

    /// <summary>
    /// A smaller, faster model for cheap sub-tasks (query planning, short summaries).
    /// Pull it with: <c>ollama pull qwen3.5:9b</c>.
    /// </summary>
    public string FastChatModel { get; set; } = "qwen3.5:9b";

    /// <summary>
    /// The embedding model used to vectorize retrieved source chunks and queries.
    /// <c>nomic-embed-text:v1.5</c> produces 768-dimensional vectors.
    /// Pull it with: <c>ollama pull nomic-embed-text:v1.5</c>.
    /// </summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text:v1.5";

    /// <summary>
    /// Placeholder credential. Ollama does not require authentication; the OpenAI client
    /// simply needs a non-empty value.
    /// </summary>
    public string ApiKey { get; set; } = "ollama";
}
