using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Ports;

public sealed record SpaceResourcePage(IReadOnlyList<SpaceFileResource> Items, int Total, int Page, int PageSize);

/// <summary>Space file metadata, parent revision, tenant IAM and audit share the existing transaction owner.</summary>
public interface ITenantSpaceResourceStore
{
    ValueTask<SpaceFileResource?> FindSpaceResourceAsync(Guid tenant, Guid space, Guid resource, CancellationToken ct = default);
    ValueTask<SpaceResourcePage> ReadSpaceResourcesAsync(Guid tenant, Guid space, int page, int pageSize, CancellationToken ct = default);
    ValueTask<bool> TrySaveSpaceResourceAsync(TenantSecurityState next, long expectedSecurityRevision, long expectedSpaceRevision,
        SpaceFileResource value, long expectedResourceRevision, AuthorizationAudit audit, HumanSessionReference? human = null, CancellationToken ct = default);
}
