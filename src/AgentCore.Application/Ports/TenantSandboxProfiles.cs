using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantSandboxProfileRoot(Guid TenantId, string ProfileId, DateTimeOffset CreatedAt);
public sealed record TenantSandboxProfilePage(IReadOnlyList<SandboxProfile> Items, int Total, int Page, int PageSize);

// Existing append-only profile rows, authoritative ownership, IAM CAS and audit share one commit.
// This catalog port grants neither execution nor disposable verification authority.
public interface ITenantSandboxProfileStore
{
    ValueTask<SandboxProfile?> FindTenantSandboxProfileAsync(Guid tenant, string id, long? revision = null, CancellationToken ct = default);
    ValueTask<TenantSandboxProfilePage> ListTenantSandboxProfilesAsync(Guid tenant, IReadOnlyList<string>? allowedIds,
        int page, int pageSize, string search, CancellationToken ct = default);
    ValueTask<bool> TrySaveTenantSandboxProfileAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxProfileRoot root, SandboxProfile profile, long expectedProfileRevision,
        HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default);
}
