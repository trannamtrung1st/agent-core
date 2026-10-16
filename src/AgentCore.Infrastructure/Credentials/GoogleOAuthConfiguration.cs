using System.Text.Json;
using AgentCore.Application.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Credentials;

// Client configuration only. Consent, callbacks, identities, tokens and epochs remain integration-owned.
public sealed record GoogleOAuthClient(string ClientId, string ClientSecret, Uri AppUri)
{
    public override string ToString() => "Google OAuth client (redacted)";
}
public class GoogleOAuthClientOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? PublicAppUrl { get; set; }
    internal GoogleOAuthClient? ResolvedClient { get; set; }
    public GoogleOAuthClient? LegacyClient { get; set; }
    // Direct options constructed by existing callers represent the legacy client. Production resolution disables guessing.
    public bool AllowUnstampedCurrentClient { get; set; } = true;
    public Uri? AppUri => ResolvedClient?.AppUri ?? ParseAppUri(PublicAppUrl);
    public GoogleOAuthClient? Client => ResolvedClient ?? (AppUri is { } uri && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) ? new(ClientId, ClientSecret, uri) : null);
    public bool Configured => Client is not null;
    internal static Uri? ParseAppUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
        && uri.UserInfo == "" && uri.AbsolutePath == "/" && uri.Query == "" && uri.Fragment == "" ? uri : null;
    public (GoogleOAuthClient Client, string Token) RefreshGrant(string payload, string integration)
    {
        var grant = GoogleOAuthRefreshGrant.Read(payload);
        var client = grant.ClientId is null ? LegacyClient ?? (AllowUnstampedCurrentClient ? Client : null)
            : Client?.ClientId == grant.ClientId ? Client : LegacyClient?.ClientId == grant.ClientId ? LegacyClient : null;
        if (client is null) throw new AgentCoreException(integration + "_oauth_client_changed",
            "The saved Google grant belongs to an unavailable OAuth client. Restore its integration-specific legacy client settings or explicitly reconnect this account. The saved grant was retained.", 409);
        return (client, grant.Token);
    }
}
public static class GoogleOAuthRefreshGrant
{
    private const string Prefix = "agent-core.google-oauth.v1:";
    private sealed record Grant(string ClientId, string Token);
    public static string Write(string token, string clientId) => Prefix + JsonSerializer.Serialize(new Grant(clientId, token));
    public static (string? ClientId, string Token) Read(string payload)
    {
        if (!payload.StartsWith(Prefix, StringComparison.Ordinal)) return (null, payload);
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
    // Canonical tenant consent never falls back to a legacy owner's integration client.
    public GoogleOAuthClient? Tenant { get; }
    public Email.GoogleEmailOAuthOptions Email { get; }
    public Drive.GoogleDriveOAuthOptions Drive { get; }
    public GoogleOAuthConfiguration(IConfiguration configuration, ILogger<GoogleOAuthConfiguration>? logger = null)
    {
        var shared = Read(configuration, "GoogleOAuth", "GOOGLE_OAUTH");
        Tenant = Client(shared);
        var email = Read(configuration, "GoogleEmailOAuth", "GOOGLE_EMAIL_OAUTH");
        var drive = Read(configuration, "GoogleDriveOAuth", "GOOGLE_DRIVE_OAUTH");
        var sharedPresent = Present(shared);
        var conflictingLegacy = Present(email) && Present(drive) && email != drive;
        if (conflictingLegacy) logger?.LogWarning("Google OAuth configuration: legacy_client_difference. Configure GoogleOAuth for new connections; retain each legacy integration client until its saved grants are migrated.");
        if (Present(email) || Present(drive)) logger?.LogWarning("Google OAuth configuration: legacy_settings_present. Legacy integration clients are retained for saved grants; canonical GoogleOAuth settings control new consent when provided.");
        var fallback = Present(email) ? email : drive;
        Email = Options<Email.GoogleEmailOAuthOptions>(sharedPresent ? shared : conflictingLegacy ? email : fallback, email);
        Drive = Options<Drive.GoogleDriveOAuthOptions>(sharedPresent ? shared : conflictingLegacy ? drive : fallback, drive);
    }
    private sealed record Settings(string? Id, string? Secret, string? Url);
    private static bool Present(Settings s) => s.Id is not null || s.Secret is not null || s.Url is not null;
    private static Settings Read(IConfiguration c, string section, string env) => new(
        Field(c, section, "ClientId", env + "_CLIENT_ID"), Field(c, section, "ClientSecret", env + "_CLIENT_SECRET"), Field(c, section, "PublicAppUrl", env + "_PUBLIC_APP_URL"));
    private static string? Field(IConfiguration c, string section, string name, string env)
    {
        var canonical = string.IsNullOrWhiteSpace(c[section + ":" + name]) ? null : c[section + ":" + name];
        var external = string.IsNullOrWhiteSpace(c[env]) ? null : c[env];
        if (canonical is not null && external is not null && canonical != external)
            throw new InvalidOperationException($"Google OAuth configuration conflict: {section}:{name} and {env} differ. Set one consistent value. Values are redacted.");
        return canonical ?? external;
    }
    private static GoogleOAuthClient? Client(Settings s)
    {
        if (s.Url is not null && GoogleOAuthClientOptions.ParseAppUri(s.Url) is null)
            throw new InvalidOperationException("Google OAuth PublicAppUrl must be an HTTPS origin or loopback HTTP origin without credentials, path, query or fragment. Values are redacted.");
        return s.Id is not null && s.Secret is not null && GoogleOAuthClientOptions.ParseAppUri(s.Url) is { } uri ? new(s.Id, s.Secret, uri) : null;
    }
    private static T Options<T>(Settings current, Settings legacy) where T : GoogleOAuthClientOptions, new() => new()
    {
        ClientId = current.Id, ClientSecret = current.Secret, PublicAppUrl = current.Url,
        ResolvedClient = Client(current), LegacyClient = Client(legacy), AllowUnstampedCurrentClient = false
    };
}
