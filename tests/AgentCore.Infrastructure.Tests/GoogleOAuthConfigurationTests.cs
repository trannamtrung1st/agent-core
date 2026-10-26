using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Credentials;
using Microsoft.Extensions.Configuration;
namespace AgentCore.Infrastructure.Tests;
public sealed class GoogleOAuthConfigurationTests
{
    private static GoogleOAuthConfiguration Config(params (string Key, string Value)[] values) => new(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());
    [Fact]
    public void Canonical_client_is_shared_and_optional_until_configured()
    {
        Assert.Null(Config().Tenant);
        var c = Config(("GoogleOAuth:ClientId", "shared"), ("GoogleOAuth:ClientSecret", "secret"), ("GoogleOAuth:PublicAppUrl", "https://app.example.test"));
        Assert.Equal("shared", c.Tenant!.ClientId);
        Assert.Equal("https://app.example.test/", c.Tenant.AppUri.AbsoluteUri);
        Assert.DoesNotContain("secret", c.Tenant.ToString());
        Assert.Throws<InvalidOperationException>(() => Config(("GoogleOAuth:ClientId", "shared")));
    }
    [Theory]
    [InlineData("plain-old-refresh")]
    [InlineData("agent-core.google-oauth.v1:{\"ClientId\":null,\"Token\":\"old-refresh\"}")]
    [InlineData("agent-core.google-oauth.v1:invalid")]
    public void Unproven_grants_require_explicit_reconnect(string payload)
    {
        var error = Assert.Throws<AgentCoreException>(() => GoogleOAuthRefreshGrant.Read(payload));
        Assert.Contains("reconnect", error.Message); Assert.DoesNotContain("old-refresh", error.Message);
    }
    [Theory]
    [InlineData("GoogleEmailOAuth:ClientId")]
    [InlineData("GoogleDriveOAuth:ClientId")]
    [InlineData("GOOGLE_EMAIL_OAUTH_CLIENT_SECRET")]
    [InlineData("GOOGLE_DRIVE_OAUTH_CLIENT_SECRET")]
    [InlineData("GOOGLE_OAUTH_CLIENT_SECRET")]
    public void Retired_or_untranslated_inputs_fail_with_names_only(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Config((key, "private-sentinel")));
        Assert.Contains(key, error.Message); Assert.DoesNotContain("private-sentinel", error.Message);
    }
}
