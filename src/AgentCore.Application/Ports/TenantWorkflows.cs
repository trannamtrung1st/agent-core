using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Ports;

public sealed record WorkflowPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record WorkflowCatalogWrite(TenantSecurityState NextSecurity, long ExpectedSecurityRevision,
    HumanSessionReference Human, WorkflowDefinition Definition, WorkflowVersion? Version, long ExpectedRevision,
    string RequestKey, string Fingerprint, AuthorizationAudit Audit);

/// <summary>Facets of the current IAM transaction, not an independent authority store.</summary>
public interface ITenantWorkflowCatalogStore
{
    ValueTask<WorkflowDefinition?> FindWorkflowAsync(Guid tenant, Guid workflow, CancellationToken ct = default);
    ValueTask<WorkflowVersion?> FindWorkflowVersionAsync(Guid tenant, Guid workflow, int version, CancellationToken ct = default);
    ValueTask<WorkflowPage<WorkflowDefinition>> ListWorkflowsAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int size, CancellationToken ct = default);
    ValueTask<WorkflowCatalogReceipt?> FindWorkflowCatalogRequestAsync(Guid tenant, Guid actor, string key, CancellationToken ct = default);
    ValueTask<WorkflowCatalogReceipt?> WriteWorkflowCatalogAsync(WorkflowCatalogWrite command, CancellationToken ct = default);
    ValueTask<WorkflowPublicationIntent> PrepareWorkflowPublicationAsync(WorkflowCatalogWrite command, CancellationToken ct = default);
}
