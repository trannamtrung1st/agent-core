using System.Text.Json;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class HttpRequestToolTests
{
    [Fact]
    public void Normalization_rejects_credentials_and_private_hosts_before_approval()
    {
        using var credential = JsonDocument.Parse(
            """{"method":"GET","url":"https://example.com/items","headers":{"Authorization":"Bearer secret"}}""");
        Assert.False(HttpRequestNormalizer.TryNormalize(credential.RootElement, out _, out var code, out _));
        Assert.Equal("forbidden_header", code);

        using var local = JsonDocument.Parse("""{"method":"GET","url":"http://127.0.0.1/latest"}""");
        Assert.False(HttpRequestNormalizer.TryNormalize(local.RootElement, out _, out var hostCode, out _));
        Assert.Equal("forbidden_host", hostCode);
    }

    [Fact]
    public void Normalization_rejects_malformed_content_type_before_approval()
    {
        using var malformed = JsonDocument.Parse(
            """
            {"method":"POST","url":"https://example.com/items","headers":{"Content-Type":"application/json; charset=\"utf-8"},"body":"{}"}
            """);
        Assert.False(HttpRequestNormalizer.TryNormalize(malformed.RootElement, out _, out var code, out _));
        Assert.Equal("invalid", code);
    }

}
