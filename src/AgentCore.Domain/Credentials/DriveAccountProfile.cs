using System.Net.Mail;
namespace AgentCore.Domain.Credentials;

public enum DriveConnectionStatus { NotConnected, Connected, ReconnectRequired, Disconnected }
public sealed record DriveAccountProfile(string EmailAddress = "", string? GoogleSubject = null, IReadOnlyList<string>? GrantedScopes = null,
    DriveConnectionStatus ConnectionStatus = DriveConnectionStatus.NotConnected, long AuthEpoch = 0,
    DateTimeOffset? ConnectedAtUtc = null, DateTimeOffset? ReconnectRequiredAtUtc = null, bool Synthetic = false)
{
    public const string Scope = "https://www.googleapis.com/auth/drive.file";
    public bool IsUsable => ConnectionStatus == DriveConnectionStatus.Connected && GrantedScopes?.Contains(Scope) == true;
    public DriveAccountProfile Validate()
    {
        if (!Enum.IsDefined(ConnectionStatus) || AuthEpoch < 0 || EmailAddress is null || EmailAddress.Length > 254 || EmailAddress.Any(char.IsControl)
            || EmailAddress.Length > 0 && (!MailAddress.TryCreate(EmailAddress, out var address) || address.Address != EmailAddress)
            || GoogleSubject is { Length: 0 or > 255 } || GoogleSubject?.Any(char.IsControl) == true
            || GrantedScopes?.Count > 8 || GrantedScopes?.Any(scope => scope is null || scope.Length > 256 || scope.Any(char.IsControl) || scope.StartsWith("https://www.googleapis.com/auth/drive", StringComparison.Ordinal) && scope != Scope) == true
            || ConnectionStatus == DriveConnectionStatus.Connected && (string.IsNullOrWhiteSpace(GoogleSubject) || EmailAddress.Length == 0 || AuthEpoch < 1 || GrantedScopes?.Contains(Scope) != true))
            throw new ArgumentException("Invalid Google Drive account identity, scope or authorization state.");
        return this;
    }
}
