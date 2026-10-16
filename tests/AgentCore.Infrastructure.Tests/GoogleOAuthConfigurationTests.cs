using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Credentials;
using Microsoft.Extensions.Configuration;
namespace AgentCore.Infrastructure.Tests;
public sealed class GoogleOAuthConfigurationTests
{
    private static GoogleOAuthConfiguration Config(params (string Key, string Value)[] values) => new(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());
    [Fact]
    public void Canonical_uppercase_settings_are_shared_but_callbacks_and_legacy_refresh_clients_are_independent()
    {
        var config = Config(("GOOGLE_OAUTH_CLIENT_ID", "shared"), ("GOOGLE_OAUTH_CLIENT_SECRET", "secret"), ("GOOGLE_OAUTH_PUBLIC_APP_URL", "https://app.example.test"),
            ("GoogleEmailOAuth:ClientId", "old-email"), ("GoogleEmailOAuth:ClientSecret", "old-email-secret"), ("GoogleEmailOAuth:PublicAppUrl", "https://app.example.test"),
            ("GoogleDriveOAuth:ClientId", "old-drive"), ("GoogleDriveOAuth:ClientSecret", "old-drive-secret"), ("GoogleDriveOAuth:PublicAppUrl", "https://app.example.test"));
        Assert.True(config.Email.Configured); Assert.True(config.Drive.Configured); Assert.Equal(config.Email.Client, config.Drive.Client);
        Assert.Equal("shared", config.Tenant!.ClientId);
        Assert.Equal("old-email", config.Email.RefreshGrant("unstamped-email", "email").Client.ClientId);
        Assert.Equal("old-drive", config.Drive.RefreshGrant("unstamped-drive", "drive").Client.ClientId);
        Assert.Equal("shared", config.Drive.RefreshGrant(GoogleOAuthRefreshGrant.Write("new-drive", "shared"), "drive").Client.ClientId);
        Assert.Equal("old-drive", config.Drive.RefreshGrant(GoogleOAuthRefreshGrant.Write("old-token", "old-drive"), "drive").Client.ClientId);
        Assert.Throws<AgentCoreException>(() => config.Email.RefreshGrant(GoogleOAuthRefreshGrant.Write("drive-token", "old-drive"), "email"));
        Assert.NotEqual(AgentCore.Infrastructure.Email.GoogleEmailOAuthOptions.CallbackPath, AgentCore.Infrastructure.Drive.GoogleDriveOAuthOptions.CallbackPath);
    }
    [Theory]
    [InlineData("plain-old-refresh")]
    [InlineData("agent-core.google-oauth.v1:{\"ClientId\":\"retired\",\"Token\":\"old-refresh\"}")]
    public void Unknown_grant_client_requires_explicit_reconnect_without_trying_new_client(string payload)
    {
        var c = Config(("GoogleOAuth:ClientId", "new"), ("GoogleOAuth:ClientSecret", "new-secret"), ("GoogleOAuth:PublicAppUrl", "http://127.0.0.1:5186"));
        Assert.Equal("drive_oauth_client_changed", Assert.Throws<AgentCoreException>(() => c.Drive.RefreshGrant(payload, "drive")).Code);
        Assert.Equal("email_oauth_client_changed", Assert.Throws<AgentCoreException>(() => c.Email.RefreshGrant(payload, "email")).Code);
    }
    [Fact]
    public void Conflicting_aliases_fail_with_redacted_field_diagnostic()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Config(("GoogleOAuth:ClientSecret", "private-one"), ("GOOGLE_OAUTH_CLIENT_SECRET", "private-two")));
        Assert.Contains("GoogleOAuth:ClientSecret", ex.Message); Assert.DoesNotContain("private-one", ex.Message); Assert.DoesNotContain("private-two", ex.Message);
    }
    [Fact]
    public void Distinct_legacy_clients_are_preserved_without_arbitrary_cross_selection()
    {
        var c = Config(("GoogleEmailOAuth:ClientId", "email"), ("GoogleEmailOAuth:ClientSecret", "email-secret"), ("GoogleEmailOAuth:PublicAppUrl", "https://app.example.test"),
            ("GoogleDriveOAuth:ClientId", "drive"), ("GoogleDriveOAuth:ClientSecret", "drive-secret"), ("GoogleDriveOAuth:PublicAppUrl", "https://app.example.test"));
        Assert.Equal("email", c.Email.Client!.ClientId); Assert.Equal("drive", c.Drive.Client!.ClientId);
        Assert.Null(c.Tenant);
    }
}
