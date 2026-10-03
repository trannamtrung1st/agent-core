namespace AgentCore.Domain.Connections;

public static class ApplicationConnectionKinds
{
    public const string NopCommerce = "nopCommerce";
}

public enum WebhookCredentialStatus
{
    NotConfigured = 0,
    Active = 1,
    Revoked = 2
}

public enum ApplicationConnectionStatus
{
    NotConnected = 0,
    Connecting = 1,
    Connected = 2,
    NeedsReauthentication = 3,
    Unavailable = 4
}

public static class ApplicationConnectionDetails
{
    public const string SignInRequired = "sign_in_required";
    public const string LoginWall = "login_wall";
    public const string HumanVerification = "human_verification";
    public const string PageUnknown = "page_unknown";
    public const string BrowserUnavailable = "browser_unavailable";
    public const string ProfileReset = "profile_reset";

    public static bool IsAllowed(string? detail) =>
        detail is null
        or SignInRequired
        or LoginWall
        or HumanVerification
        or PageUnknown
        or BrowserUnavailable
        or ProfileReset;
}

public sealed record ApplicationConnection(
    Guid ConnectionId,
    Guid AgentInstanceId,
    string Kind,
    string DisplayName,
    string BaseUrl,
    IReadOnlyList<string> TrustedOrigins,
    ApplicationConnectionStatus Status,
    Guid ProfileKey,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? StatusDetail,
    Guid? WebhookKey = null,
    string? WebhookTokenHash = null,
    WebhookCredentialStatus WebhookStatus = WebhookCredentialStatus.NotConfigured);
