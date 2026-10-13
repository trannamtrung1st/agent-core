namespace AgentCore.Application.Ports;

public sealed record DriveOAuthStart(string AuthorizationUrl, string Correlation, DateTimeOffset ExpiresAtUtc, bool SecureCookie, bool Synthetic);
public sealed record DriveOAuthConnection(string Status, bool Configured, bool Synthetic, string? Error = null, DateTimeOffset? LastCheckedAtUtc = null, bool? ProviderRevoked = null);
public interface IGoogleDriveOAuthService
{
    ValueTask<DriveOAuthStart> StartAsync(Guid accountId, long revision, Guid ownerId, CancellationToken ct);
    ValueTask<Guid> CompleteAsync(string state, string? correlation, Guid ownerId, string? code, string? error, CancellationToken ct);
    ValueTask<DriveOAuthConnection> ConnectionAsync(Guid accountId, CancellationToken ct);
    ValueTask<DriveOAuthConnection> CheckAsync(Guid accountId, long revision, CancellationToken ct);
    ValueTask<DriveOAuthConnection> DisconnectAsync(Guid accountId, long revision, CancellationToken ct);
    string ReturnUrl { get; }
    bool IsConfigured { get; }
    bool IsSynthetic { get; }
}
