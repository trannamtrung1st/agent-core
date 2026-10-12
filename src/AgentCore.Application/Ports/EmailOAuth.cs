namespace AgentCore.Application.Ports;

public sealed record EmailOAuthStart(string AuthorizationUrl, string Correlation, DateTimeOffset ExpiresAtUtc, bool SecureCookie, bool Synthetic);
public sealed record EmailOAuthConnection(string Status, bool Configured, bool Synthetic, string? Error,
    DateTimeOffset? LastCheckedAtUtc = null, bool? ProviderRevoked = null);
public interface IEmailOAuthService
{
    ValueTask<EmailOAuthStart> StartAsync(Guid accountId, long revision, Guid ownerId, CancellationToken ct);
    ValueTask<Guid> CompleteAsync(string state, string? correlation, Guid ownerId, string? code, string? error, CancellationToken ct);
    ValueTask<EmailOAuthConnection> ConnectionAsync(Guid accountId, CancellationToken ct);
    ValueTask<EmailOAuthConnection> CheckAsync(Guid accountId, long revision, CancellationToken ct);
    ValueTask<EmailOAuthConnection> DisconnectAsync(Guid accountId, long revision, CancellationToken ct);
    // Fixed, operator-configured destination; never derived from callback query or forwarded headers.
    string ReturnUrl { get; }
    bool IsConfigured { get; }
    bool IsSynthetic { get; }
}
public interface IEmailSendPreflight
{
    ValueTask PrepareSendAsync(CancellationToken ct);
}
