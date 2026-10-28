using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Workflows;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantWorkflowCatalogStore
{
    private readonly Dictionary<(Guid Tenant, Guid Actor, string Key), WorkflowPublicationIntent> workflowPublications = [];
    private readonly Dictionary<(Guid Tenant, Guid Id), WorkflowDefinition> workflowDefinitions = [];
    private readonly Dictionary<(Guid Tenant, Guid Id, int Version), WorkflowVersion> workflowVersions = [];
    private readonly Dictionary<(Guid Tenant, Guid Actor, string Key), WorkflowCatalogReceipt> workflowCatalogReceipts = [];
    public ValueTask<WorkflowDefinition?> FindWorkflowAsync(Guid tenant, Guid workflow, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(workflowDefinitions.GetValueOrDefault((tenant, workflow))); }
    public ValueTask<WorkflowVersion?> FindWorkflowVersionAsync(Guid tenant, Guid workflow, int version, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(workflowVersions.GetValueOrDefault((tenant, workflow, version))); }
    public ValueTask<WorkflowPage<WorkflowDefinition>> ListWorkflowsAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int size, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) { var all = workflowDefinitions.Values.Where(w => w.TenantId == tenant && (allowed is null || allowed.Contains(w.WorkflowId)))
            .OrderBy(w => w.WorkflowId.ToString("D"), StringComparer.Ordinal).ToArray(); return ValueTask.FromResult(new WorkflowPage<WorkflowDefinition>(all.Skip(page * size).Take(size).ToArray(), all.Length, page, size)); }
    }
    public ValueTask<WorkflowCatalogReceipt?> FindWorkflowCatalogRequestAsync(Guid tenant, Guid actor, string key, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(workflowCatalogReceipts.GetValueOrDefault((tenant, actor, key))); }
    public ValueTask<WorkflowPublicationIntent> PrepareWorkflowPublicationAsync(WorkflowCatalogWrite command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); WorkflowCatalogAuthority.Command(command);
        if (command.Version is null || command.Audit.Action != "workflows.publish") throw new ArgumentException("An immutable publication is required.");
        lock (_gate) lock (instances.CredentialGate)
        {
            var tenant = command.Definition.TenantId; var key = (tenant, command.Human.PrincipalId, command.RequestKey);
            RequireHumanSession(tenant, command.Human);
            var state = _tenants.GetValueOrDefault(tenant) ?? throw new AccessDeniedException("tenant_unavailable");
            WorkflowCatalogAuthority.RequireAsync(state, command, false, new(this), ct).GetAwaiter().GetResult();
            if (workflowPublications.TryGetValue(key, out var previous)) {
                if (previous.Fingerprint != command.Fingerprint) throw new SecurityConflictException("workflow_request_conflict"); return ValueTask.FromResult(previous); }
            var before = workflowDefinitions.GetValueOrDefault((tenant, command.Definition.WorkflowId));
            if (state.Tenant.Revision != command.ExpectedSecurityRevision || before?.Revision != command.ExpectedRevision) throw new SecurityConflictException("stale_revision");
            WorkflowCatalogRules.Change(before, command.Definition, command.Version, command.ExpectedRevision, command.Audit.Action, command.Human.PrincipalId);
            var intent = new WorkflowPublicationIntent(tenant, command.Definition.WorkflowId, command.Human.PrincipalId, command.RequestKey, command.Fingerprint, command.ExpectedRevision, command.Version);
            workflowPublications.Add(key, intent); _audit.Add(command.Audit); return ValueTask.FromResult(intent);
        }
    }
    public ValueTask<WorkflowCatalogReceipt?> WriteWorkflowCatalogAsync(WorkflowCatalogWrite command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); WorkflowCatalogAuthority.Command(command);
        lock (_gate) lock (instances.CredentialGate)
        {
            var tenant = command.Definition.TenantId; var id = command.Definition.WorkflowId;
            RequireHumanSession(tenant, command.Human);
            if (!_tenants.TryGetValue(tenant, out var state)) throw new AccessDeniedException("tenant_unavailable");
            WorkflowCatalogAuthority.RequireAsync(state, command, command.Audit.Action == "workflows.create", new(this), ct).GetAwaiter().GetResult();
            if (workflowCatalogReceipts.TryGetValue((tenant, command.Human.PrincipalId, command.RequestKey), out var previous))
            {
                if (previous.Fingerprint != command.Fingerprint) throw new SecurityConflictException("workflow_request_conflict");
                return ValueTask.FromResult<WorkflowCatalogReceipt?>(previous);
            }
            var before = workflowDefinitions.GetValueOrDefault((tenant, id));
            if (state.Tenant.Revision != command.ExpectedSecurityRevision || (before?.Revision ?? 0) != command.ExpectedRevision) return ValueTask.FromResult<WorkflowCatalogReceipt?>(null);
            WorkflowCatalogRules.Change(before, command.Definition, command.Version, command.ExpectedRevision, command.Audit.Action, command.Human.PrincipalId);
            if (before is null && workflowDefinitions.Values.Count(w => w.TenantId == tenant) >= 200) throw new ArgumentException("Workflow catalog limit reached.");
            var result = new WorkflowCatalogReceipt(tenant, command.Human.PrincipalId, command.RequestKey, command.Fingerprint, command.Audit.Action,
                command.Definition, command.Version, state.Tenant.Revision + 1);
            if (command.Version is { } version)
            {
                if (!workflowPublications.TryGetValue((tenant, command.Human.PrincipalId, command.RequestKey), out var intent)
                    || intent.Fingerprint != command.Fingerprint || intent.DraftRevision != command.ExpectedRevision || intent.Version != version)
                    throw new AccessDeniedException("workflow_publication_not_prepared");
                workflowVersions.Add((tenant, id, version.Version), version);
            }
            workflowDefinitions[(tenant, id)] = command.Definition;
            workflowCatalogReceipts.Add((tenant, command.Human.PrincipalId, command.RequestKey), result);
            _tenants[tenant] = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }; _audit.Add(command.Audit);
            return ValueTask.FromResult<WorkflowCatalogReceipt?>(result);
        }
    }
}
