using System.Text.Json;
using AgentCore.Infrastructure.Email;

namespace AgentCore.Infrastructure.Tests;

public sealed class GmailRedactionTests
{
    [Fact]
    public void Redact_removes_bearer_and_token_fields_from_json()
    {
        const string raw =
            """{"access_token":"ya29.secret-value","refresh_token":"1//refresh","token_type":"Bearer"}""";
        var redacted = GmailSensitiveRedactor.Redact(raw);
        Assert.DoesNotContain("ya29.secret-value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("1//refresh", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(redacted);
        Assert.Equal("[redacted]", document.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("[redacted]", document.RootElement.GetProperty("refresh_token").GetString());
    }

    [Fact]
    public void Redact_removes_authorization_header_text()
    {
        const string raw = "Authorization: Bearer ya29.header-token-value";
        var redacted = GmailSensitiveRedactor.Redact(raw);
        Assert.DoesNotContain("ya29.header-token-value", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
    }
}
