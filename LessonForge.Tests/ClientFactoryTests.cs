using LessonForge.Core.Configuration;
using Xunit;

namespace LessonForge.Tests;

public sealed class ClientFactoryTests
{
    [Fact]
    public void LocalInference_DefaultsAllowLongRequestsWithoutAutomaticRetries()
    {
        var options = new LlmOptions();

        Assert.Equal(600, options.NetworkTimeoutSeconds);
        Assert.Equal(8192, options.MaxOutputTokens);
        Assert.Equal(0, options.MaxRetries);

        // Creating the client does not contact Ollama; verifies the configuration is accepted.
        var factory = new ClientFactory(options);
        Assert.Same(options, factory.Options);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidNetworkTimeout_IsRejected(int seconds)
    {
        var options = new LlmOptions { NetworkTimeoutSeconds = seconds };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientFactory(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidOutputTokenBudget_IsRejected(int tokens)
    {
        var options = new LlmOptions { MaxOutputTokens = tokens };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientFactory(options));
    }

    [Fact]
    public void NegativeRetryCount_IsRejected()
    {
        var options = new LlmOptions { MaxRetries = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientFactory(options));
    }

    [Fact]
    public void ExplicitTimeoutAndRetrySettings_AreAccepted()
    {
        var options = new LlmOptions { NetworkTimeoutSeconds = 900, MaxRetries = 1, MaxOutputTokens = 16384 };

        var factory = new ClientFactory(options);
        Assert.Same(options, factory.Options);
    }
}
