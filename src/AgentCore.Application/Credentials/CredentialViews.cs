using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Credentials;

public sealed record CredentialView(Guid CredentialId, string DisplayName, string Kind, string Status,
    IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<string> AllowedOrigins, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, int BindingCount,
    string Purpose = "General", EmailAccountProfile? EmailAccount = null, DriveAccountProfile? DriveAccount = null, ContainerRegistryProfile? ContainerRegistry = null);
public sealed record CredentialBindingView(Guid BindingId, Guid CredentialId, string Reference, long Revision, CredentialView Credential);
public sealed record BoundCredentialMetadata(string Reference, string DisplayName, string Kind, IReadOnlyDictionary<string, string> Metadata);

internal static class CredentialProjection
{
    internal static CredentialView View(Credential c, int count) => new(c.CredentialId, c.DisplayName, c.Kind.ToString(), c.Status.ToString(),
        c.Metadata, c.AllowedOrigins, c.Revision, c.CreatedAtUtc, c.UpdatedAtUtc, count, c.Purpose.ToString(), c.EmailAccount, c.DriveAccount, c.ContainerRegistry);
}
