using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantAgent?> FindTenantAgentAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (instances.CredentialGate)
        {
            var identity = _tenants.GetValueOrDefault(tenantId)?.SystemIdentities.SingleOrDefault(i => i.AgentInstanceId == instanceId && i.Status != IdentityStatus.Removed);
            var instance = identity is null ? null : instances.FindTenantBackingAsync(instanceId, ct).Result;
            return ValueTask.FromResult(instance is null || identity is null ? null : new TenantAgent(tenantId, identity.SystemIdentityId, identity.PrincipalId, instance, identity.ManagedKind));
        }
    }

    public ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedInstanceIds, int page, int pageSize, CancellationToken ct = default, TenantAgentChatFilter? chat = null)
    {
        ct.ThrowIfCancellationRequested();
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) lock (instances.CredentialGate)
        {
            var allowed = allowedInstanceIds?.ToHashSet();
            var rows = (_tenants.GetValueOrDefault(tenantId)?.SystemIdentities ?? []).Where(i => i.Kind == SystemIdentityKind.Agent && i.Status != IdentityStatus.Removed
                && i.AgentInstanceId is { } id && (allowed is null || allowed.Contains(id)))
                .Select(i => new TenantAgent(tenantId, i.SystemIdentityId, i.PrincipalId, instances.FindTenantBackingAsync(i.AgentInstanceId!.Value, ct).Result
                    ?? throw new InvalidOperationException("An owned agent lost its backing Instance."), i.ManagedKind))
                .Where(i => chat is null || i.Instance.Lifecycle == AgentInstanceLifecycle.Active
                    && (chat.InstanceId is null || chat.InstanceId == i.Instance.InstanceId) && chat.Matches(i.Instance.Persona))
                .OrderByDescending(i => i.ManagedKind != null).ThenBy(i => i.Instance.InstanceId.ToString("D"), StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new TenantAgentPage(rows.Skip(page * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize));
        }
    }

    public ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        CreateTenantAgentCoreAsync(next, expectedSecurityRevision, instance, skills, audit, false, ct, initiator);

    public ValueTask<bool> TryProvisionGeneralAssistantAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default) =>
        CreateTenantAgentCoreAsync(next, expectedSecurityRevision, instance, skills, audit, true, ct);

    private ValueTask<bool> CreateTenantAgentCoreAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, bool managedProvision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instance.InstanceId, instance.Lifecycle, audit);
        ManagedGeneralAssistant.ValidateProvision(next, instance, audit, managedProvision);
        if (instance.Revision != 1 || instance.Lifecycle != AgentInstanceLifecycle.Active || instance.BrowserProfileReset is not null) throw new ArgumentException("A new active Instance is required.");
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
            ManagedGeneralAssistant.ValidateMarkerAdditions(before, next, managedProvision ? instance.InstanceId : null);
            if (!managedProvision) ValidateAgentActor(before, audit.ActorPrincipalId);
            EnsureReferences(next, audit, instance.InstanceId, ct);
            if (instances.FindTenantBackingAsync(instance.InstanceId, ct).Result is not null) return ValueTask.FromResult(false);
            RequireAgentHuman(next.Tenant.TenantId, audit.ActorPrincipalId, initiator);
            instances.InsertAsync(instance, ct, skills).GetAwaiter().GetResult();
            ProvisionPrivateSpace(next.Tenant.TenantId, instance);
            if (managedProvision)
            {
                var owner = next.SystemIdentities.Single(i => i.AgentInstanceId == instance.InstanceId);
                var space = _spaces.Values.Single(s => s.TenantId == next.Tenant.TenantId && s.OwnerInstanceId == instance.InstanceId && s.DeletedAt is null).SpaceId;
                var role = next.Roles.Single(r => r.Key == ManagedGeneralAssistant.FilesRole);
                next = IamStateRules.Freeze(next with { Assignments = next.Assignments.Append(new RoleAssignment(Guid.NewGuid(), next.Tenant.TenantId,
                    owner.PrincipalId, role.RoleId, TenantWorkspaceRules.Home(next.Tenant.TenantId, space), Guid.Empty, instance.CreatedAt)).Append(new RoleAssignment(Guid.NewGuid(), next.Tenant.TenantId,
                    owner.PrincipalId, role.RoleId, AccessScope.ForResource(next.Tenant.TenantId, "space", space.ToString("D")), Guid.Empty, instance.CreatedAt)).ToArray() });
            }
            _tenants[next.Tenant.TenantId] = next; MarkOwnedAgents(next); _audit.Add(audit);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid instanceId, long expectedInstanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null,
        IReadOnlyList<AgentCore.Domain.Conversation.ConversationEntry>? responses = null)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instanceId, lifecycle, audit);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantAgent?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
                ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit, ct: ct);
            var current = instances.FindTenantBackingAsync(instanceId, ct).Result;
            if (!before.SystemIdentities.Any(i => i.AgentInstanceId == instanceId && i.Status != IdentityStatus.Removed) || current is null || current.Revision != expectedInstanceRevision) return ValueTask.FromResult<TenantAgent?>(null);
            RequireAgentHuman(next.Tenant.TenantId, audit.ActorPrincipalId, initiator);
            var updated = instances.SetTenantLifecycleAsync(instanceId, expectedInstanceRevision, lifecycle, at, ct).Result;
            if (lifecycle == AgentInstanceLifecycle.Disabled)
            {
                foreach (var run in sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == next.Tenant.TenantId
                    && r.AgentInstanceId == instanceId && !r.IsTerminal).ToArray())
                {
                    var saved = sessionBacking.CancelTenantRun(next.Tenant.TenantId, run.AgentRunId, run.Revision, at, responses?.SingleOrDefault(e => e.ResponseId == run.ResponseId), "agentDisabled")!;
                    AppendTenantCoreRun(run, saved); SettleTenantCompletion(saved, run.Claim?.Generation);
                }
                foreach (var task in delegatedTasks.Values.Where(t => t.Source.TenantId == next.Tenant.TenantId
                    && t.State == DelegatedTaskState.PendingDependencies
                    && (t.Assignment.InstanceId == instanceId || t.Source.InstanceId == instanceId)).ToArray())
                {
                    var blockedTask = task.Advance(DelegatedTaskState.Blocked, at, reason: "agent_disabled", cancellationRequested: true);
                    delegatedTasks[(task.Source.TenantId, task.TaskId)] = blockedTask;
                    if (task.Source.SessionId is { } sourceSession)
                        taskNotifications[(task.Source.TenantId, task.TaskId)] = new(task.Source.TenantId, Guid.NewGuid(), task.TaskId, null, sourceSession, 1, at);
                }
                foreach (var row in agentMessages.Values.Where(m => m.Message.TenantId == next.Tenant.TenantId
                    && (m.Message.RecipientInstanceId == instanceId || m.Message.SenderInstanceId == instanceId)
                    && m.Receipt.State == AgentMessageDelivery.NotificationPending).ToArray())
                    agentMessages[(next.Tenant.TenantId, row.Message.MessageId)] = (row.Message,
                        row.Receipt with { State = AgentMessageDelivery.Undeliverable, Reason = "agent_disabled", Revision = checked(row.Receipt.Revision + 1), UpdatedAt = at }, row.Scope);
                foreach (var delivery in sessionBacking.CoreEvents.TenantDeliveries().Where(d => d.Subscription.Owner.TenantId == next.Tenant.TenantId
                    && d.Subscription.Owner.AgentInstanceId == instanceId && (d.Status is EventMatchStatus.Pending or EventMatchStatus.Matched or EventMatchStatus.Coalesced or EventMatchStatus.AwaitingAdmission
                        || d.Status == EventMatchStatus.FilterError && d.Decision?.Retryable == true)))
                    sessionBacking.CoreEvents.PutTenantDelivery(delivery with { Status = EventMatchStatus.PolicySkipped, Code = "agent_disabled" });
                foreach (var delivery in webhookBacking.TenantDeliveries().Where(d => d.Snapshot!.Owner.TenantId == next.Tenant.TenantId
                    && d.AgentInstanceId == instanceId && (d.Status is ExternalEventDeliveryStatus.Pending or ExternalEventDeliveryStatus.Coalesced or ExternalEventDeliveryStatus.AwaitingAdmission
                        || d.Status == ExternalEventDeliveryStatus.FilterError && d.Decision?.Retryable == true)))
                    webhookBacking.PutTenantDelivery(delivery with { Status = ExternalEventDeliveryStatus.Skipped, Code = "agent_disabled" });
                foreach (var bucket in sessionBacking.CoreEvents.TenantBuckets().Where(b => b.Subscription.Owner.TenantId == next.Tenant.TenantId
                    && b.Subscription.Owner.AgentInstanceId == instanceId && !b.Flushed))
                    sessionBacking.CoreEvents.PutTenantBucket(bucket with { Flushed = true, CompletionCode = "agent_disabled" });
                var requests = _approvals.Values.Where(r => r.TenantId == next.Tenant.TenantId && r.Operation.InstanceId == instanceId).ToArray();
                foreach (var request in requests)
                {
                    if (request.Status == TenantApprovalStatus.Pending)
                        _approvals[request.RequestId] = request.Decide(at >= request.ExpiresAt ? TenantApprovalStatus.Expired : TenantApprovalStatus.Cancelled, request.Revision, at);
                    if (_operationGrants.GetValueOrDefault(request.RequestId) is { Status: OperationGrantStatus.Available } grant)
                        _operationGrants[request.RequestId] = grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) };
                }
            }
            if (lifecycle == AgentInstanceLifecycle.Active && current.Lifecycle == AgentInstanceLifecycle.Disabled)
                foreach (var automation in automationBacking.TenantAutomations(next.Tenant.TenantId).Where(a => a.Owner.AgentInstanceId == instanceId))
                    automationBacking.SaveTenantAutomation(AgentAvailabilityPolicy.SkipMissedSchedule(automation, at));
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
            var identity = next.SystemIdentities.Single(i => i.AgentInstanceId == instanceId && i.Status != IdentityStatus.Removed);
            return ValueTask.FromResult<TenantAgent?>(new(next.Tenant.TenantId, identity.SystemIdentityId, identity.PrincipalId, updated, identity.ManagedKind));
        }
    }
    private void RequireAgentHuman(Guid tenant, Guid actor, HumanSessionReference? initiator)
    {
        if (initiator is null) return;
        if (initiator.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
        try { RequireHumanSession(tenant, initiator); }
        catch (AccessDeniedException) { throw new AccessDeniedException("initiator_unavailable"); }
    }
    private void ValidateAgentActor(TenantSecurityState before, Guid actorId)
    {
        var actor = before.Principals.SingleOrDefault(p => p.PrincipalId == actorId && p.Status == IdentityStatus.Active)
            ?? throw new AccessDeniedException("principal_unavailable");
        if (actor.Kind == PrincipalKind.User && _accounts.GetValueOrDefault(actor.AccountId!.Value)?.Status != IdentityStatus.Active)
            throw new AccessDeniedException("account_unavailable");
    }
}
