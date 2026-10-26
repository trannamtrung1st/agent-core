using System.Text.Json;
using AgentCore.Application.Sessions;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Infrastructure.Credentials;

// Client configuration only. Consent, callbacks, identities, tokens and epochs remain integration-owned.
public sealed record GoogleOAuthClient(string ClientId, string ClientSecret, Uri AppUri)
{
    public override string ToString() => "Google OAuth client (redacted)";
}
public static class GoogleOAuthRefreshGrant
{
    private const string Prefix = "agent-core.google-oauth.v1:";
    private sealed record Grant(string ClientId, string Token);
    public static string Write(string token, string clientId) => Prefix + JsonSerializer.Serialize(new Grant(clientId, token));
    public static (string ClientId, string Token) Read(string payload)
    {
        if (!payload.StartsWith(Prefix, StringComparison.Ordinal))
            throw new AgentCoreException("google_oauth_grant_invalid", "Saved Google authorization has no proven client binding. Explicitly reconnect this account.", 409);
        try
        {
            var value = JsonSerializer.Deserialize<Grant>(payload[Prefix.Length..]);
            if (value is null || value.ClientId.Length is < 1 or > 2048 || value.Token.Length is < 1 or > 8192) throw new JsonException();
            return (value.ClientId, value.Token);
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException)
        { throw new AgentCoreException("google_oauth_grant_invalid", "Saved Google authorization cannot be interpreted. Explicitly reconnect this account.", 409); }
    }
}
public sealed class GoogleOAuthConfiguration
{
    public GoogleOAuthClient? Tenant { get; }
    public GoogleOAuthConfiguration(IConfiguration configuration)
    {
        Configuration.ConfigurationCutover.Validate(configuration);
        var id = Value(configuration, "ClientId");
        var secret = Value(configuration, "ClientSecret");
        var url = Value(configuration, "PublicAppUrl");
        if (id is null && secret is null && url is null) return;
        if (id is null || secret is null || ParseAppUri(url) is not { } uri)
            throw new InvalidOperationException("GoogleOAuth requires ClientId, ClientSecret and an HTTPS origin (or loopback HTTP), without credentials, path, query or fragment. Values are redacted.");
        Tenant = new(id, secret, uri);
    }
    private static string? Value(IConfiguration c, string field) =>
        string.IsNullOrWhiteSpace(c["GoogleOAuth:" + field]) ? null : c["GoogleOAuth:" + field];
    internal static Uri? ParseAppUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
        && uri.UserInfo == "" && uri.AbsolutePath == "/" && uri.Query == "" && uri.Fragment == "" ? uri : null;
}
