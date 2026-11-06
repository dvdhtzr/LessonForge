using System.Text.Json;
using LessonForge.Core.Configuration;
using Xunit;

namespace LessonForge.Tests;

public sealed class CommittedConfigurationTests
{
    private static JsonDocument LoadSettings()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CommittedAppsettings.json");
        return JsonDocument.Parse(File.ReadAllText(file));
    }

    [Fact]
    public void CommittedModels_MatchDocumentedLocalDefaults()
    {
        using var document = LoadSettings();
        var llm = document.RootElement.GetProperty("LessonForge").GetProperty("Llm");
        var defaults = new LlmOptions();

        Assert.Equal("gemma4:latest", llm.GetProperty("ChatModel").GetString());
        Assert.Equal("nomic-embed-text:v1.5", llm.GetProperty("EmbeddingModel").GetString());
        Assert.Equal(defaults.ChatModel, llm.GetProperty("ChatModel").GetString());
        Assert.Equal(defaults.EmbeddingModel, llm.GetProperty("EmbeddingModel").GetString());
        Assert.Equal(defaults.Endpoint, llm.GetProperty("Endpoint").GetString());
        Assert.Equal(defaults.MaxOutputTokens, llm.GetProperty("MaxOutputTokens").GetInt32());
        Assert.Equal(defaults.NetworkTimeoutSeconds, llm.GetProperty("NetworkTimeoutSeconds").GetInt32());
        Assert.Equal(defaults.MaxRetries, llm.GetProperty("MaxRetries").GetInt32());
    }

    [Fact]
    public void CommittedConfiguration_DoesNotRequireCredentialsOrUnusedModels()
    {
        using var document = LoadSettings();
        var llm = document.RootElement.GetProperty("LessonForge").GetProperty("Llm");

        Assert.False(llm.TryGetProperty("ApiKey", out _));
        Assert.False(llm.TryGetProperty("FastChatModel", out _));
    }
}
