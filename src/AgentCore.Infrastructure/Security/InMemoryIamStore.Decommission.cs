using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantAgentDecommissionStore
{
    private TenantAgentDecommissionSnapshot? DecommissionSnapshot(Guid tenant, Guid instance)
    {
        var identity = _tenants.GetValueOrDefault(tenant)?.SystemIdentities.SingleOrDefault(i => i.AgentInstanceId == instance);
        var row = identity is null ? null : instances.FindTenantBackingAsync(instance).Result;
        if (identity is null || row is null) return null;
        var triggers = instances.TriggerStore?.CountForInstance(instance) ?? (Registrations: 0, Occurrences: 0);
        var runs = definitionReferences.AgentRuns.Runs.Values.Where(r => r.AgentInstanceId == instance).ToArray();
        var references = new AdminDeletionReferenceCounts(definitionReferences.CountLiveByInstance(instance), (structuredMemories?.CountActiveIdentityUser(instance) ?? 0) + learnedMemories.Values.Count(i => i.TenantId == tenant && i.AgentInstanceId == instance && i.Status != AgentCore.Domain.Memory.MemoryItemStatus.Deleted),
            triggers.Registrations, triggers.Occurrences, runs.Length, runs.Count(r => r.Status == AgentCore.Domain.Conversation.AgentRunStatus.WaitingForApproval), runs.Length, 0);
        return new(new(tenant, identity.SystemIdentityId, identity.PrincipalId, row), identity.Status, references,
            _audit.SingleOrDefault(a => a.TenantId == tenant && a.Action == TenantAgentDecommissionRules.Action && a.Target == instance.ToString("D")));
    }
    public ValueTask<TenantAgentDecommissionSnapshot?> ReadAgentDecommissionAsync(Guid tenant, Guid instance, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate) lock (instances.TriggerStore?.AdmissionGate ?? _gate) lock (definitionReferences.AdmissionGate)
            return ValueTask.FromResult(DecommissionSnapshot(tenant, instance));
    }
    public ValueTask<bool> TryDecommissionAgentAsync(Guid tenant, Guid instance, HumanSessionReference human, long expectedSecurityRevision,
        long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantAgentDecommissionRules.Validate(tenant, instance, human, audit);
        lock (_gate) lock (instances.CredentialGate) lock (instances.TriggerStore?.AdmissionGate ?? _gate) lock (definitionReferences.AdmissionGate)
        lock (structuredMemories?.SyncRoot ?? _gate)
        {
            RequireHumanSession(tenant, human);
            var snapshot = DecommissionSnapshot(tenant, instance); var state = _tenants.GetValueOrDefault(tenant);
            if (snapshot is null || state is null || !TenantAgentDecommissionRules.Allowed(state, human.PrincipalId, instance, new IamAuthorizer(this), true, ct).Result) return ValueTask.FromResult(false);
            if (_audit.SingleOrDefault(a => a.AuditId == audit.AuditId) is { } receipt)
                return ValueTask.FromResult(snapshot.Status == IdentityStatus.Removed && snapshot.Receipt == receipt && receipt.ActorPrincipalId == human.PrincipalId
                    && snapshot.Agent.Instance.Revision == expectedInstanceRevision + 1);
            if (state.Tenant.Revision != expectedSecurityRevision || snapshot.Agent.Instance.Revision != expectedInstanceRevision || snapshot.Status == IdentityStatus.Removed) return ValueTask.FromResult(false);
            if (snapshot.Agent.Instance.Lifecycle != AgentInstanceLifecycle.Archived) throw new SecurityConflictException("archive_required");
            if (snapshot.References.HasReferences) throw new SecurityConflictException("agent_in_use");
            if (snapshot.Agent.Instance.BrowserProfileReset?.Status == "pending") throw new SecurityConflictException("browser_profile_busy");
            var privateHome = _spaces.Values.Single(s => s.TenantId == tenant && s.OwnerInstanceId == instance && s.DeletedAt is null);
            var changedSpaces = _spaceMemberships.Keys.Where(k => k.Tenant == tenant && k.Instance == instance).Select(k => _spaces[(k.Tenant, k.Space)]).Select(s =>
                s with { Revision = checked(s.Revision + 1), UpdatedAt = audit.At, DeletedAt = s.SpaceId == privateHome.SpaceId ? audit.At : s.DeletedAt,
                    EnvironmentsEnabled = s.SpaceId == privateHome.SpaceId ? false : s.EnvironmentsEnabled,
                    EnvironmentSelectionEpoch = s.SpaceId == privateHome.SpaceId && s.EnvironmentsEnabled ? checked(s.EnvironmentSelectionEpoch + 1) : s.EnvironmentSelectionEpoch }).ToArray();
            var changedEnvironments = _environments.Values.Where(e => e.TenantId == tenant && e.SpaceId == privateHome.SpaceId && e.DeletedAt is null)
                .Select(e => e with { Revision = checked(e.Revision + 1), UpdatedAt = audit.At, DeletedAt = audit.At }).ToArray();
            var next = TenantAgentDecommissionRules.Retire(state, instance, snapshot.Agent.PrincipalId, privateHome.SpaceId);
            _ = instances.SetTenantLifecycleAsync(instance, expectedInstanceRevision, AgentInstanceLifecycle.Archived, audit.At, ct).Result;
            instances.PurgeSkills(instance); credentials?.RetireTenantBindings(tenant, instance);
            foreach (var space in changedSpaces) _spaces[(tenant, space.SpaceId)] = space;
            foreach (var member in _spaceMemberships.Keys.Where(k => k.Tenant == tenant && k.Instance == instance).ToArray()) _spaceMemberships.Remove(member);
            foreach (var profile in changedEnvironments) _environments[(tenant, profile.SpaceId, profile.EnvironmentId)] = profile;
            foreach (var variable in _variables.Where(v => v.Value.TenantId == tenant && v.Value.SpaceId == privateHome.SpaceId).Select(v => v.Key).ToArray()) _variables.Remove(variable);
            _tenants[tenant] = next; _audit.Add(audit);
            return ValueTask.FromResult(true);
        }
    }
    public ValueTask<TenantAgentDecommissionPage> ListAgentDecommissionCleanupAsync(Guid? afterInstance, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate) lock (instances.CredentialGate) {
            var all = _tenants.Values.SelectMany(t => t.SystemIdentities.Where(i => i.Status == IdentityStatus.Removed && i.AgentInstanceId is not null)
                .Select(i => new TenantBrowserAgentOwner(t.Tenant.TenantId, i.AgentInstanceId!.Value, i.PrincipalId)))
                .Where(o => (afterInstance is null || string.CompareOrdinal(o.AgentInstanceId.ToString("D"), afterInstance.Value.ToString("D")) > 0) && IsDecommissioned(o))
                .OrderBy(o => o.AgentInstanceId.ToString("D"), StringComparer.Ordinal).Take(limit + 1).ToArray();
            return ValueTask.FromResult(new TenantAgentDecommissionPage(all.Take(limit).ToArray(), all.Length > limit ? all[limit - 1].AgentInstanceId : null));
        }
    }
    private bool IsDecommissioned(TenantBrowserAgentOwner owner)
    {
        var state = _tenants.GetValueOrDefault(owner.TenantId);
        var identity = state?.SystemIdentities.SingleOrDefault(i => i.AgentInstanceId == owner.AgentInstanceId && i.PrincipalId == owner.ExecutorPrincipalId && i.Status == IdentityStatus.Removed);
        return identity is not null && state!.Principals.Any(p => p.PrincipalId == owner.ExecutorPrincipalId && p.Status == IdentityStatus.Removed)
            && instances.FindTenantBackingAsync(owner.AgentInstanceId).Result?.Lifecycle == AgentInstanceLifecycle.Archived
            && _audit.Any(a => a.TenantId == owner.TenantId && a.Action == TenantAgentDecommissionRules.Action && a.Target == owner.AgentInstanceId.ToString("D") && a.ReasonCode == "agent_decommissioned");
    }
    public ValueTask<bool> IsAgentDecommissionedAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
            return ValueTask.FromResult(IsDecommissioned(owner));
    }
    public ValueTask<TenantWorkspaceReference.SpaceHome?> ReadDecommissionedPrivateWorkspaceAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            var space = IsDecommissioned(owner) ? _spaces.Values.SingleOrDefault(s => s.TenantId == owner.TenantId && s.OwnerInstanceId == owner.AgentInstanceId && s.DeletedAt is not null) : null;
            return ValueTask.FromResult(space is null ? null : new TenantWorkspaceReference.SpaceHome(owner.TenantId, space.SpaceId));
        }
    }
    public ValueTask<bool> CanPurgeDecommissionedAgentWorkspaceAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
            return ValueTask.FromResult(IsDecommissioned(owner) && !sandboxBacking.HasUnretiredInstanceLease(owner.AgentInstanceId));
    }
}
