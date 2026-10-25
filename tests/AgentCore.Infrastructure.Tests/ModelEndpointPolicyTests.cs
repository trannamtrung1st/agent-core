using System.Net;
using AgentCore.Application.Models;
using AgentCore.Domain.Models;

namespace AgentCore.Infrastructure.Tests;

public sealed class ModelEndpointPolicyTests
{
    [Theory]
    [InlineData("http://api.example.test/v1/")]
    [InlineData("https://user:secret@api.example.test/v1/")]
    [InlineData("https://api.example.test/v1/?key=secret")]
    [InlineData("https://api.example.test/v1/#secret")]
    [InlineData("http://127.0.0.1:8080/v1/")]
    [InlineData("https://169.254.169.254/v1/")]
    [InlineData("https://metadata.google.internal/v1/")]
    public void Rejects_untrusted_custom_endpoints(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new ModelEndpointPolicy().Validate(ModelProviderKind.OpenAICompatible, endpoint, false));

    [Fact]
    public void Local_allowance_is_host_owned_exact_and_never_permits_metadata_or_rebinding()
    {
        var policy = new ModelEndpointPolicy(["http://localhost:8080", "http://models.internal:8080"]);
        Assert.Equal("http://localhost:8080/v1/", policy.Validate(ModelProviderKind.OpenAICompatible, "http://localhost:8080/v1", true));
        Assert.Throws<ArgumentException>(() => policy.Validate(ModelProviderKind.OpenAICompatible, "http://localhost:9090/v1", true));
        Assert.True(policy.AllowsAddress(new("http://models.internal:8080/v1/"), IPAddress.Parse("192.168.1.8")));
        Assert.False(policy.AllowsAddress(new("https://public.example.test/v1/"), IPAddress.Parse("192.168.1.8")));
        Assert.False(policy.AllowsAddress(new("http://models.internal:8080/v1/"), IPAddress.Parse("169.254.169.254")));
        Assert.False(policy.AllowsAddress(new("http://models.internal:8080/v1/"), IPAddress.Parse("169.254.170.2")));
        Assert.Throws<ArgumentException>(() => new ModelEndpointPolicy(["http://169.254.169.254"]));
        Assert.Throws<ArgumentException>(() => policy.Validate(ModelProviderKind.OpenAI, "https://other.example.test/v1/", false));
        Assert.Throws<ArgumentException>(() => policy.Validate(ModelProviderKind.Anthropic, null, true));
    }
}
