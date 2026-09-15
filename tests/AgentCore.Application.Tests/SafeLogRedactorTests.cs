using AgentCore.Application.Observability;

namespace AgentCore.Application.Tests;

public sealed class SafeLogRedactorTests
{
    [Theory]
    [InlineData("Bearer sk-abc123", "[redacted]")]
    [InlineData("https://provider.example/v1/chat?api_key=secret", "https://provider.example/v1/chat")]
    [InlineData("AdditionalHeaders: x", "[redacted]")]
    [InlineData("pcm_s16le frame", "pcm_s16le frame")]
    public void Redacts_secrets_and_query_strings(string input, string expected)
    {
        Assert.Equal(expected, SafeLogRedactor.Redact(input));
    }
}
