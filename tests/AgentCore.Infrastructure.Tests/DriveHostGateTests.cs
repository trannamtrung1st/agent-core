using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Drive;
using AgentCore.Infrastructure.Tools;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Infrastructure.Tests;

public sealed class DriveHostGateTests
{
    [Theory]
    [InlineData(false, false, false, false)] [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)] [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)] [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)] [InlineData(true, true, true, true)]
    public void Drive_readiness_uses_current_canonical_descriptor_without_account_resolution(bool synthetic, bool enabled, bool configured, bool expected)
    {
        var settings = configured ? new Dictionary<string, string?> { ["GoogleOAuth:ClientId"] = "current-client", ["GoogleOAuth:ClientSecret"] = "operator-secret", ["GoogleOAuth:PublicAppUrl"] = "https://app.example.test" } : [];
        var options = new GoogleDriveOptions { Enabled = enabled };
        // Describe is pure configuration metadata; no proof/protected credential
        // dependency or HTTP operation may be used by readiness.
        var provider = new TenantGoogleOAuthProvider(new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()), options, null!, null!, new NoHttp(), synthetic);
        var gate = new ToolConfigurationGate(null, null, oauth: provider);
        foreach (var tool in new[] { "drive.accounts", "drive.list", "drive.upload", "drive.trash" }) Assert.Equal(expected, gate.IsConfigured(tool));
        options.Enabled = false; Assert.False(gate.IsConfigured("drive.list"));
    }

    private sealed class NoHttp : IHttpClientFactory
    { public HttpClient CreateClient(string name) => throw new InvalidOperationException("Readiness cannot perform HTTP or resolve account secrets."); }
}
