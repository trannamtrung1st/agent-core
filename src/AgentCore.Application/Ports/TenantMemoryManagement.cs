using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantMemoryQuery(MemoryScope Scope, Guid? SessionId = null, int Page = 1, int PageSize = 25, Guid? MemoryId = null);
public sealed record TenantMemoryItemView(TenantLearnedMemory Memory, bool CanForget);
public sealed record TenantMemoryPage(IReadOnlyList<TenantMemoryItemView> Items, int Total, int RetainedCount, int Page, int PageSize,
    long SecurityRevision, bool CanReset, string AgentName);
public sealed record TenantMemoryForget(TenantMemoryQuery Query, long ExpectedSecurityRevision, Guid? MemoryId = null,
    long? ExpectedMemoryRevision = null, int? ExpectedRetainedCount = null);
public sealed record TenantMemoryForgetResult(int ItemsForgotten, long SecurityRevision);

// Original live human, current raw reads and actual User ownership. Runtime policy
// does not hide retained data from its authorized human owner or grant a model effect.
public interface ITenantMemoryManagementStore
{
    ValueTask<TenantMemoryPage> ReadOwnTenantMemoryAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantMemoryQuery query, CancellationToken ct = default);
    ValueTask<TenantMemoryForgetResult?> TryForgetOwnTenantMemoryAsync(Guid tenant, Guid agent, HumanSessionReference human,
        TenantMemoryForget change, AuthorizationAudit audit, CancellationToken ct = default);
}
