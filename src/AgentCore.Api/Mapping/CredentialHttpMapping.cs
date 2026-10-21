using AgentCore.Application.Credentials;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;

namespace AgentCore.Api.Mapping;

internal static class CredentialHttpMapping
{
    private static EmailAccountProfileDto Map(EmailAccountProfile value) => new(value.Preset.ToString(), value.EmailAddress,
        value.ImapUsername, value.SmtpUsername, value.ImapEndpoint is { } imap ? new(imap.Host, imap.Port, imap.TlsMode.ToString()) : null,
        value.SmtpEndpoint is { } smtp ? new(smtp.Host, smtp.Port, smtp.TlsMode.ToString()) : null, value.AuthMethod.ToString(),
        value.GoogleSubject, value.GrantedScopes, value.ConnectionStatus.ToString(), value.AuthEpoch, value.ConnectedAtUtc, value.Synthetic);
    internal static AdminCredentialResponse Map(CredentialView c) => new(c.CredentialId, c.DisplayName, c.Kind, c.Status, c.Metadata, c.AllowedOrigins, c.Revision, c.CreatedAtUtc, c.UpdatedAtUtc, c.BindingCount, c.Purpose, c.EmailAccount is null ? null : Map(c.EmailAccount), c.DriveAccount is null ? null : new(c.DriveAccount.EmailAddress, c.DriveAccount.ConnectionStatus.ToString(), c.DriveAccount.AuthEpoch, c.DriveAccount.ConnectedAtUtc, c.DriveAccount.ReconnectRequiredAtUtc, c.DriveAccount.Synthetic), c.ContainerRegistry is { } registry ? new(registry.Registry, registry.Username, registry.RepositoryPrefixes, registry.ExpiresAtUtc) : null);
}
