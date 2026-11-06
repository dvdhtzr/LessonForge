// Copyright (c) 2026 — LessonForge. Original work.

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace LessonForge.Core.Configuration;

/// <summary>
/// Builds the chat clients and embedding generator the whole app runs on, all bound to a local
/// Ollama server via its OpenAI-compatible API. Uses the configured chat model
/// for each workflow agent and a separate embedding generator.
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
        ArgumentNullException.ThrowIfNull(options);
        if (options.NetworkTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.NetworkTimeoutSeconds), "Must be positive.");
        if (options.MaxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxRetries), "Cannot be negative.");
        if (options.MaxOutputTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxOutputTokens), "Must be positive.");

        Options = options;
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.Endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(options.NetworkTimeoutSeconds),
            RetryPolicy = new ClientRetryPolicy(options.MaxRetries)
        };
        // Ollama ignores the API key, but the OpenAI client requires a non-empty credential.
        _client = new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions);
    }

    private static readonly Lazy<ClientFactory> s_default = new(
        () => new ClientFactory(LoadOptions()), isThreadSafe: true);

    /// <summary>A process-wide factory configured from <c>appsettings.json</c> + environment.</summary>
    public static ClientFactory Default => s_default.Value;

    /// <summary>Loads <see cref="LlmOptions"/> from the local appsettings.json file.</summary>
    public static LlmOptions LoadOptions()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var options = new LlmOptions();
        configuration.GetSection(LlmOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// A chat client bound to the model configured in the repository's appsettings.json.
    /// </summary>
    public IChatClient CreateChatClient()
        => _client.GetChatClient(Options.ChatModel).AsIChatClient();

    /// <summary>An embedding generator bound to Ollama's <see cref="LlmOptions.EmbeddingModel"/>.</summary>
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator()
        => _client.GetEmbeddingClient(Options.EmbeddingModel).AsIEmbeddingGenerator();
}
