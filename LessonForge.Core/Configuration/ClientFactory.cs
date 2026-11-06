// Copyright (c) 2026 — LessonForge. Original work.

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace LessonForge.Core.Configuration;

/// <summary>
/// Builds the chat clients and embedding generator the whole app runs on, all bound to a local
/// Ollama server via its OpenAI-compatible API. Provides a dual-model setup (primary + fast chat)
/// plus an embedding generator.
///
/// Usable two ways:
///  - DI: register a single <see cref="ClientFactory"/> instance (see the API host) and inject it.
///  - Static: <see cref="Default"/> lazily loads options from <c>appsettings.json</c>/env vars.
/// </summary>
public sealed class ClientFactory
{
    private readonly OpenAIClient _client;

    public LlmOptions Options { get; }

    public ClientFactory(LlmOptions options)
    {
        Options = options;
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) };
        // Ollama ignores the API key, but the OpenAI client requires a non-empty credential.
        _client = new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions);
    }

    private static readonly Lazy<ClientFactory> s_default = new(
        () => new ClientFactory(LoadOptions()), isThreadSafe: true);

    /// <summary>A process-wide factory configured from <c>appsettings.json</c> + environment.</summary>
    public static ClientFactory Default => s_default.Value;

    /// <summary>Loads <see cref="LlmOptions"/> from config, honoring <c>LessonForge__Llm__*</c> env vars.</summary>
    public static LlmOptions LoadOptions()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var options = new LlmOptions();
        configuration.GetSection(LlmOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// A chat client bound to Ollama. <paramref name="fast"/> selects the smaller
    /// <see cref="LlmOptions.FastChatModel"/>; otherwise the primary <see cref="LlmOptions.ChatModel"/>.
    /// </summary>
    public IChatClient CreateChatClient(bool fast = false)
    {
        var model = fast ? Options.FastChatModel : Options.ChatModel;
        return _client.GetChatClient(model).AsIChatClient();
    }

    /// <summary>An embedding generator bound to Ollama's <see cref="LlmOptions.EmbeddingModel"/>.</summary>
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator()
        => _client.GetEmbeddingClient(Options.EmbeddingModel).AsIEmbeddingGenerator();
}
