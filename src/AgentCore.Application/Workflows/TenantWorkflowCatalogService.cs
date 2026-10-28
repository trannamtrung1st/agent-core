using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public sealed record WorkflowDraftWrite(string Name, string Description, WorkflowGraph Graph, bool Retired,
    long ExpectedRevision, long ExpectedSecurityRevision, string RequestKey);
public sealed record WorkflowPublish(long ExpectedRevision, long ExpectedSecurityRevision, string RequestKey);
public sealed record AvailableWorkflow(Guid WorkflowId, string Name, string Description, int PublishedVersion, string GraphHash);
public sealed record WorkflowCatalogControls(WorkflowDefinition? Definition, long SecurityRevision, bool CanCreate, bool CanUpdate, bool CanPublish, bool CanStart);
public sealed record WorkflowValidation(IReadOnlyList<WorkflowDiagnostic> Diagnostics, string? Hash, long SecurityRevision);

public sealed class TenantWorkflowCatalogService(IIamStore security, ITenantWorkflowCatalogStore store,
    ITenantAgentStore agents, ITenantSpaceStore spaces, ITenantEnvironmentStore environments, ITenantEvaluationStore evaluations,
    IamAuthorizer authorizer, IdentityService identity, IIdGenerator ids, TimeProvider time,
    IFormalWorkflowRuntime? runtime = null, WorkflowEvidenceAuthority? evidence = null)
{
    private WorkflowEvidenceAuthority Evidence => evidence ?? new((ITenantWorkflowExecutionStore)security, evaluations, (ITenantSessionGoalStore)security,
        (ITenantRunStore)security, (ITenantSessionStore)security, (ITenantTaskStore)security, agents, authorizer, time);
    public async ValueTask<WorkflowPage<WorkflowDefinition>> ListAsync(Guid tenant, HumanSessionReference human, int page, int size, CancellationToken ct = default)
    {
        var state = await State(tenant, human, ct);
        var all = await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("workflows.read"), AccessScope.ForTenant(tenant), ct);
        List<Guid>? allowed = null;
        if (!all.Allowed)
        {
            if (all.ReasonCode != "permission_denied") throw new AccessDeniedException(all.ReasonCode);
            allowed = [];
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == human.PrincipalId).Select(a => a.Scope).Distinct())
                if (WorkflowCatalogAuthority.TryScope(scope, out var id) && (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("workflows.read"), scope, ct)).Allowed) allowed.Add(id);
        }
        var result = await store.ListWorkflowsAsync(tenant, allowed, page, size, ct); await Current(state, human, ct); return result;
    }
    public async ValueTask<WorkflowPage<AvailableWorkflow>> AvailableHumanAsync(Guid tenant, HumanSessionReference human, int page, int size, CancellationToken ct = default)
    {
        if (page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentException("Use a bounded Workflow discovery page.");
        var state = await State(tenant, human, ct); var eligible = new List<AvailableWorkflow>();
        for (var sourcePage = 0; sourcePage < 2; sourcePage++)
        {
            var definitions = await ListAsync(tenant, human, sourcePage, 100, ct);
            foreach (var definition in definitions.Items.Where(d => !d.Retired && d.PublishedVersion > 0))
            {
                var scope = WorkflowCatalogAuthority.Scope(tenant, definition.WorkflowId);
                if (!(await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("workflows.start"), scope, ct)).Allowed) continue;
                var version = await store.FindWorkflowVersionAsync(tenant, definition.WorkflowId, definition.PublishedVersion, ct);
                if (version is null || !version.Graph.Read().AllowedOrigins.Contains(WorkflowInvocationKind.HumanManual)
                    || (await ValidateBindings(state, definition.WorkflowId, human.PrincipalId, version.Graph.Read(), ct)).Count != 0) continue;
                eligible.Add(new(definition.WorkflowId, definition.Name, definition.Description, version.Version, version.Graph.Hash));
            }
            if ((sourcePage + 1) * 100 >= definitions.Total) break;
        }
        await Current(state, human, ct); return new(eligible.Skip(page * size).Take(size).ToArray(), eligible.Count, page, size);
    }
    public async ValueTask<WorkflowCatalogControls> ControlsAsync(Guid tenant, Guid? workflow, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await State(tenant, human, ct);
        var value = workflow is { } id ? await Read(state, id, human, ct) : null;
        async ValueTask<bool> Can(string key, AccessScope scope) => (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(key), scope, ct)).Allowed;
        var create = await Can("workflows.create", AccessScope.ForTenant(tenant));
        var scope = value is null ? AccessScope.ForTenant(tenant) : WorkflowCatalogAuthority.Scope(tenant, value.WorkflowId);
        var result = new WorkflowCatalogControls(value, state.Tenant.Revision, create, value is not null && await Can("workflows.update", scope),
            value is { Retired: false } && await Can("workflows.publish", scope), value is { Retired: false, PublishedVersion: > 0 } && await Can("workflows.start", scope));
        await Current(state, human, ct); return result;
    }
    public async ValueTask<WorkflowDefinition> ReadAsync(Guid tenant, Guid id, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await State(tenant, human, ct); var result = await Read(state, id, human, ct); await Current(state, human, ct); return result;
    }
    public async ValueTask<WorkflowVersion> VersionAsync(Guid tenant, Guid id, int version, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await State(tenant, human, ct); await Read(state, id, human, ct);
        var result = await store.FindWorkflowVersionAsync(tenant, id, version, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await Current(state, human, ct); return result;
    }
    public async ValueTask<WorkflowCatalogReceipt?> RecoverAsync(Guid tenant, HumanSessionReference human, string key, CancellationToken ct = default)
    {
        _ = WorkflowCatalogRules.RequestKey(key); var state = await State(tenant, human, ct);
        var receipt = await store.FindWorkflowCatalogRequestAsync(tenant, human.PrincipalId, key, ct);
        if (receipt is not null) await Read(state, receipt.Definition.WorkflowId, human, ct);
        await Current(state, human, ct); return receipt;
    }
    public async ValueTask<WorkflowCatalogReceipt> SaveAsync(Guid tenant, Guid id, HumanSessionReference human, WorkflowDraftWrite request, CancellationToken ct = default)
    {
        var state = await State(tenant, human, ct); var before = await store.FindWorkflowAsync(tenant, id, ct);
        var action = request.ExpectedRevision == 0 ? "workflows.create" : "workflows.update";
        await Require(state, human.PrincipalId, action, action == "workflows.create" ? AccessScope.ForTenant(tenant) : WorkflowCatalogAuthority.Scope(tenant, id), ct);
        if (before is not null) await Read(state, id, human, ct);
        var fingerprint = Fingerprint(new { action, id, request.Name, request.Description, Draft = WorkflowCatalogRules.Draft(request.Graph), request.Retired, request.ExpectedRevision });
        if (await Replay(tenant, human, request.RequestKey, fingerprint, ct) is { } replay) { await Current(state, human, ct); return replay; }
        if ((before?.Revision ?? 0) != request.ExpectedRevision) throw new SecurityConflictException("stale_revision");
        var now = time.GetUtcNow(); var value = new WorkflowDefinition(tenant, id, request.Name, request.Description,
            WorkflowCatalogRules.Draft(request.Graph), request.ExpectedRevision + 1, before?.PublishedVersion ?? 0, request.Retired,
            before?.CreatedBy ?? human.PrincipalId, before?.CreatedAt ?? now, now);
        WorkflowCatalogRules.Change(before, value, null, request.ExpectedRevision, action, human.PrincipalId);
        return await Write(state, human, value, null, request.ExpectedRevision, request.ExpectedSecurityRevision, request.RequestKey, fingerprint, action, ct);
    }
    public async ValueTask<WorkflowValidation> ValidateAsync(Guid tenant, Guid id, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await State(tenant, human, ct); var value = await Read(state, id, human, ct);
        var graph = WorkflowGraphRules.Parse(value.DraftJson); var diagnostics = await ValidateBindings(state, id, human.PrincipalId, graph, ct);
        await Current(state, human, ct); return new(diagnostics, diagnostics.Count == 0 ? WorkflowGraphRules.ValidateAndFreeze(graph).Hash : null, state.Tenant.Revision);
    }
    public async ValueTask<WorkflowCatalogReceipt> PublishAsync(Guid tenant, Guid id, HumanSessionReference human, WorkflowPublish request, CancellationToken ct = default)
    {
        var state = await State(tenant, human, ct); var before = await Read(state, id, human, ct);
        await Require(state, human.PrincipalId, "workflows.publish", WorkflowCatalogAuthority.Scope(tenant, id), ct);
        var fingerprint = Fingerprint(new { action = "workflows.publish", id, request.ExpectedRevision });
        if (await Replay(tenant, human, request.RequestKey, fingerprint, ct) is { } replay) { await Current(state, human, ct); return replay; }
        if (before.Revision != request.ExpectedRevision || before.Retired) throw new SecurityConflictException("workflow_not_publishable");
        if (state.Tenant.Revision != request.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        var graph = WorkflowGraphRules.Parse(before.DraftJson); var diagnostics = await ValidateBindings(state, id, human.PrincipalId, graph, ct);
        if (diagnostics.Count != 0) throw new ArgumentException("Workflow validation failed: " + string.Join("; ", diagnostics.Select(d => d.Code + ":" + d.NodeId + " " + d.Message)));
        var now = time.GetUtcNow(); var version = new WorkflowVersion(tenant, id, before.PublishedVersion + 1, WorkflowGraphRules.ValidateAndFreeze(graph), human.PrincipalId, now);
        var value = before with { Revision = before.Revision + 1, PublishedVersion = version.Version, UpdatedAt = now };
        var intent = await store.PrepareWorkflowPublicationAsync(new(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } },
            request.ExpectedSecurityRevision, human, value, version, before.Revision, request.RequestKey, fingerprint,
            new(ids.NewId(), tenant, human.PrincipalId, "workflows.publish", id.ToString("D"), "allowed", "publication_prepared", now)), ct);
        version = intent.Version; value = value with { UpdatedAt = version.PublishedAt };
        if (runtime is not null)
        {
            if (!await runtime.IsReadyAsync(ct)) throw new AccessDeniedException("workflow_runtime_unavailable");
            await runtime.EnsureCompiledAsync(version, ct);
            await Current(state, human, ct);
        }
        return await Write(state, human, value, version, before.Revision, request.ExpectedSecurityRevision, request.RequestKey, fingerprint, "workflows.publish", ct);
    }
    public async ValueTask<WorkflowVersion> EligibleVersionAsync(Guid tenant, Guid id, int version, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await State(tenant, human, ct); var definition = await Read(state, id, human, ct);
        if (definition.Retired) throw new AccessDeniedException("workflow_version_unavailable");
        var value = await store.FindWorkflowVersionAsync(tenant, id, version, ct) ?? throw new AccessDeniedException("workflow_version_unavailable");
        var diagnostics = await ValidateBindings(state, id, human.PrincipalId, value.Graph.Read(), ct);
        if (diagnostics.Count != 0) throw new AccessDeniedException("workflow_bindings_unavailable");
        await Current(state, human, ct); return value;
    }
    public async ValueTask<WorkflowVersion> EligibleAgentVersionAsync(Guid tenant, Guid id, int version, Guid actor, CancellationToken ct = default)
    {
        var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var definition = await store.FindWorkflowAsync(tenant, id, ct) ?? throw new AccessDeniedException("workflow_version_unavailable");
        await Require(state, actor, "workflows.read", WorkflowCatalogAuthority.Scope(tenant, id), ct);
        if (definition.Retired) throw new AccessDeniedException("workflow_version_unavailable");
        var value = await store.FindWorkflowVersionAsync(tenant, id, version, ct) ?? throw new AccessDeniedException("workflow_version_unavailable");
        if ((await ValidateBindings(state, id, actor, value.Graph.Read(), ct)).Count != 0) throw new AccessDeniedException("workflow_bindings_unavailable");
        if ((await security.ReadTenantAsync(tenant, ct))?.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return value;
    }
    private async ValueTask<IReadOnlyList<WorkflowDiagnostic>> ValidateBindings(TenantSecurityState state, Guid workflow, Guid actor, WorkflowGraph graph, CancellationToken ct)
    {
        var diagnostics = WorkflowGraphRules.Validate(graph).ToList(); if (diagnostics.Count != 0) return diagnostics;
        var tenant = state.Tenant.TenantId;
        if (graph.SuccessGoal is { } success)
        {
            try {
                var saved = await Evidence.ReadGoalBindingAsync(state, actor, success.SessionId, success.GoalId, success.PlanRevision, ct);
                if (saved.PlanRevision != success.PlanRevision) diagnostics.Add(new("goal_binding_stale", "Bind the exact current Goal plan for final success."));
            }
            catch (AccessDeniedException) { diagnostics.Add(new("goal_unavailable", "Bind a currently readable Goal for final success.")); }
        }
        foreach (var id in graph.CoordinatorInstanceIds)
            if (!await EligibleAgent(state, actor, id, ct)) diagnostics.Add(new("coordinator_unavailable", "An eligible readable coordinator is required."));
        foreach (var node in graph.Nodes)
        {
            if (node.Task is { } task)
            {
                if (!await EligibleAgent(state, actor, task.TargetInstanceId, ct)
                    || (await agents.FindTenantAgentAsync(tenant, task.TargetInstanceId, ct))?.Instance.Cooperation?.AcceptTasks != true)
                    diagnostics.Add(new("task_target_unavailable", "Select an eligible target accepting Tasks.", node.Id));
                else if (await spaces.FindSpaceAsync(tenant, task.SpaceId, ct) is not { DeletedAt: null }
                    || !(await spaces.ReadMembershipsAsync(tenant, task.SpaceId, ct)).Any(m => m.InstanceId == task.TargetInstanceId)
                    || !(await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("spaces.read"), TenantSpaceService.Scope(tenant, task.SpaceId), ct)).Allowed
                    || task.EnvironmentId is { } environment && await environments.FindEnvironmentAsync(tenant, task.SpaceId, environment, ct) is not { DeletedAt: null })
                    diagnostics.Add(new("task_context_unavailable", "Select a readable current Space and bound target membership/environment.", node.Id));
            }
            if (node.Decision is { } decision && (!state.Users.Any(u => u.PrincipalId == decision.ReviewerPrincipalId && u.Status == IdentityStatus.Active)
                || !(await authorizer.CheckOwnedTenantAsync(state, decision.ReviewerPrincipalId, PermissionCatalog.Get("workflows.decide"), WorkflowCatalogAuthority.Scope(tenant, workflow), ct)).Allowed))
                diagnostics.Add(new("reviewer_unavailable", "Assign an active human with procedural decision permission.", node.Id));
            if (node.Verification is { Kind: WorkflowVerificationKind.Evaluation } proof)
            {
                var scope = AccessScope.ForResource(tenant, "evaluation-suite", proof.SuiteId!.Value.ToString("D"));
                var suite = await evaluations.FindSuiteAsync(tenant, proof.SuiteId.Value, proof.SuiteVersion, ct);
                var current = await evaluations.FindSuiteAsync(tenant, proof.SuiteId.Value, ct: ct);
                if (suite is null || suite.Archived || suite.DraftId is not null || suite.ContentHash != proof.SuiteHash || current?.Archived != false
                    || !(await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("evaluations.read"), scope, ct)).Allowed)
                    diagnostics.Add(new("suite_unavailable", "Bind an exact readable published Evaluation Suite version/hash.", node.Id));
            }
            if (node.Verification is { Kind: WorkflowVerificationKind.GoalCheckpoint } goal)
            {
                try {
                    var saved = await Evidence.ReadGoalBindingAsync(state, actor, goal.SessionId!.Value, goal.GoalId!.Value, goal.PlanRevision!.Value, ct);
                    if (saved.PlanRevision != goal.PlanRevision || !saved.Plan.Checkpoints.Any(c => c.CheckpointId == goal.CheckpointId))
                        diagnostics.Add(new("goal_binding_stale", "Bind a checkpoint in the exact current Goal plan.", node.Id));
                }
                catch (AccessDeniedException) { diagnostics.Add(new("goal_unavailable", "Select a currently readable Goal checkpoint.", node.Id)); }
            }
        }
        return diagnostics;
    }
    private async ValueTask<bool> EligibleAgent(TenantSecurityState state, Guid actor, Guid id, CancellationToken ct)
        => state.SystemIdentities.Any(i => i.AgentInstanceId == id && i.Status == IdentityStatus.Active)
            && await agents.FindTenantAgentAsync(state.Tenant.TenantId, id, ct) is not null
            && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("agents.read"), AccessScope.ForResource(state.Tenant.TenantId, "agent", id.ToString("D")), ct)).Allowed;
    private async ValueTask<WorkflowDefinition> Read(TenantSecurityState state, Guid id, HumanSessionReference human, CancellationToken ct)
    {
        var result = await store.FindWorkflowAsync(state.Tenant.TenantId, id, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, human.PrincipalId, "workflows.read", WorkflowCatalogAuthority.Scope(state.Tenant.TenantId, id), ct); return result;
    }
    private async ValueTask<WorkflowCatalogReceipt?> Replay(Guid tenant, HumanSessionReference human, string key, string fingerprint, CancellationToken ct)
    {
        _ = WorkflowCatalogRules.RequestKey(key); var previous = await store.FindWorkflowCatalogRequestAsync(tenant, human.PrincipalId, key, ct);
        if (previous is not null && previous.Fingerprint != fingerprint) throw new SecurityConflictException("workflow_request_conflict"); return previous;
    }
    private async ValueTask<WorkflowCatalogReceipt> Write(TenantSecurityState state, HumanSessionReference human, WorkflowDefinition value,
        WorkflowVersion? version, long expected, long expectedSecurity, string key, string fingerprint, string action, CancellationToken ct)
    {
        if (state.Tenant.Revision != expectedSecurity) throw new SecurityConflictException("stale_revision");
        var audit = new AuthorizationAudit(ids.NewId(), value.TenantId, human.PrincipalId, action, value.WorkflowId.ToString("D"), "allowed", "authorized", time.GetUtcNow());
        return await store.WriteWorkflowCatalogAsync(new(state with { Tenant = state.Tenant with { Revision = expectedSecurity + 1 } }, expectedSecurity,
            human, value, version, expected, key, fingerprint, audit), ct) ?? throw new SecurityConflictException("stale_revision");
    }
    private ValueTask<TenantSecurityState> State(Guid tenant, HumanSessionReference human, CancellationToken ct)
        => TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
    private async ValueTask Current(TenantSecurityState state, HumanSessionReference human, CancellationToken ct)
    { if ((await State(state.Tenant.TenantId, human, ct)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision"); }
    private async ValueTask Require(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    { var result = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct); if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode); }
    private static string Fingerprint<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
