using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantAgent?> FindTenantAgentAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = instanceId.ToString("D");
        var row = await OwnedAgents(db, tenantId).SingleOrDefaultAsync(x => x.Instance.InstanceId == id, ct).ConfigureAwait(false);
        return row is null ? null : Owned(row);
    }

    public async ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedInstanceIds, int page, int pageSize, CancellationToken ct = default, TenantAgentChatFilter? chat = null)
    {
        if (page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        IQueryable<AgentInstanceRecord> instances = db.AgentInstances;
        if (!string.IsNullOrEmpty(chat?.Query))
        {
            // SQLite LIKE/LOWER do not preserve OrdinalIgnoreCase for Unicode labels.
            // Execute the same public-label predicate inside the count/page queries,
            // without loading every Instance or searching private persona/settings fields.
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            ((SqliteConnection)db.Database.GetDbConnection()).CreateFunction<string, string, bool>("agent_chat_matches", (json, text) =>
            {
                using var persona = JsonDocument.Parse(json);
                return persona.RootElement.GetProperty("name").GetString()!.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || persona.RootElement.GetProperty("role").GetString()!.Contains(text, StringComparison.OrdinalIgnoreCase);
            }, isDeterministic: true);
            instances = db.AgentInstances.FromSqlInterpolated($"SELECT * FROM AgentInstances WHERE agent_chat_matches(PersonaJson, {chat.Query})");
        }
        var query = OwnedAgents(db, tenantId, instances: instances);
        if (chat is not null)
        {
            query = query.Where(x => x.Instance.Lifecycle == nameof(AgentInstanceLifecycle.Active));
            if (chat.InstanceId is { } exact) query = query.Where(x => x.Instance.InstanceId == exact.ToString("D"));
        }
        if (allowedInstanceIds is not null)
        {
            var allowed = allowedInstanceIds.Select(id => id.ToString("D")).ToArray();
            query = query.Where(x => allowed.Contains(x.Instance.InstanceId));
        }
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderByDescending(x => x.Identity.ManagedKind != null).ThenBy(x => x.Instance.InstanceId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows.Select(Owned).ToArray(), total, page, pageSize);
    }

    public ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        CreateTenantAgentCoreAsync(next, expectedSecurityRevision, instance, skills, audit, false, ct, initiator);

    public ValueTask<bool> TryProvisionGeneralAssistantAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default) =>
        CreateTenantAgentCoreAsync(next, expectedSecurityRevision, instance, skills, audit, true, ct);

    private async ValueTask<bool> CreateTenantAgentCoreAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, bool managedProvision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        next = IamStateRules.Freeze(next); IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instance.InstanceId, instance.Lifecycle, audit);
        ManagedGeneralAssistant.ValidateProvision(next, instance, audit, managedProvision);
        if (instance.Revision != 1 || instance.Lifecycle != AgentInstanceLifecycle.Active || instance.BrowserProfileReset is not null) throw new ArgumentException("A new active Instance is required.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (current is null || current.Revision != expectedSecurityRevision) return false;
        var before = (await ReadTenantAsync(db, current.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
        ManagedGeneralAssistant.ValidateMarkerAdditions(before, next, managedProvision ? instance.InstanceId : null);
        if (!managedProvision) await ValidateTenantActorAsync(db, before, audit.ActorPrincipalId, ct).ConfigureAwait(false);
        await EnsureActiveAccountOwnerAsync(db, next, null, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, next, ct, instance.InstanceId).ConfigureAwait(false);
        db.AgentInstances.Add(SqliteAgentInstanceStore.Map(instance));
        ProvisionPrivateSpace(db, next.Tenant.TenantId, instance);
        if (managedProvision)
        {
            var owner = next.SystemIdentities.Single(i => i.AgentInstanceId == instance.InstanceId);
            var space = db.ChangeTracker.Entries<SpaceRecord>().Single(e => e.Entity.OwnerInstanceId == instance.InstanceId.ToString("D")).Entity.SpaceId;
            var role = next.Roles.Single(r => r.Key == ManagedGeneralAssistant.FilesRole);
            next = IamStateRules.Freeze(next with { Assignments = next.Assignments.Append(new RoleAssignment(Guid.NewGuid(), next.Tenant.TenantId,
                owner.PrincipalId, role.RoleId, TenantWorkspaceRules.Home(next.Tenant.TenantId, space), Guid.Empty, instance.CreatedAt)).Append(new RoleAssignment(Guid.NewGuid(), next.Tenant.TenantId,
                owner.PrincipalId, role.RoleId, AccessScope.ForResource(next.Tenant.TenantId, "space", space.ToString("D")), Guid.Empty, instance.CreatedAt)).ToArray() });
        }
        await SqliteAgentInstanceStore.InitializeSkillsAsync(db, instance.InstanceId, skills, instance.UpdatedAt, ct).ConfigureAwait(false);
        db.Entry(current).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        await RequireAgentHumanAsync(db, next.Tenant.TenantId, audit.ActorPrincipalId, initiator, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }

    public async ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid instanceId, long expectedInstanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null,
        IReadOnlyList<AgentCore.Domain.Conversation.ConversationEntry>? responses = null)
    {
        next = IamStateRules.Freeze(next); IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instanceId, lifecycle, audit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (current is null || current.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, current.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
        await ValidateTenantActorAsync(db, before, audit.ActorPrincipalId, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        var id = instanceId.ToString("D");
        if (!before.SystemIdentities.Any(i => i.AgentInstanceId == instanceId)) return null;
        var row = await db.AgentInstances.SingleOrDefaultAsync(i => i.InstanceId == id, ct).ConfigureAwait(false);
        if (row is null || row.Revision != expectedInstanceRevision) return null;
        var old = SqliteAgentInstanceStore.Map(row);
        row.Lifecycle = lifecycle.ToString(); row.UpdatedAtUtc = at.ToUnixTimeMilliseconds(); row.Revision++;
        db.Entry(current).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        if (lifecycle == AgentInstanceLifecycle.Disabled)
        {
            // The availability commit also fences every retained generation and wait.
            // Exact effect evidence stays on the cancelled Run for late reconciliation.
            var runs = await db.AgentRuns.Where(r => r.TenantId == next.Tenant.TenantId && r.AgentInstanceId == id
                && r.Status != (int)AgentCore.Domain.Conversation.AgentRunStatus.Completed
                && r.Status != (int)AgentCore.Domain.Conversation.AgentRunStatus.Failed
                && r.Status != (int)AgentCore.Domain.Conversation.AgentRunStatus.Cancelled).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var run in runs)
            {
                var value = AgentRunStoreMapping.ToDomain(run);
                await CancelRunAsync(db, run, value, at, Guid.NewGuid(), audit.ActorPrincipalId, ct,
                    responses?.SingleOrDefault(e => e.ResponseId == value.ResponseId), "agentDisabled").ConfigureAwait(false);
            }
        }
        if (lifecycle == AgentInstanceLifecycle.Disabled)
        {
            var tasks = await db.Set<DelegatedTaskRow>().Where(t => t.TenantId == next.Tenant.TenantId
                && t.State == (int)DelegatedTaskState.PendingDependencies).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var taskRow in tasks)
            {
                var task = TaskPersistence.Value(taskRow);
                if (task.Assignment.InstanceId != instanceId && task.Source.InstanceId != instanceId) continue;
                db.Entry(taskRow).CurrentValues.SetValues(TaskPersistence.Row(task.Advance(DelegatedTaskState.Blocked, at,
                    reason: "agent_disabled", cancellationRequested: true)));
                if (task.Source.SessionId is { } sourceSession)
                {
                    var notification = new TaskNotification(next.Tenant.TenantId, Guid.NewGuid(), task.TaskId, null, sourceSession, 1, at);
                    db.Add(new TaskNotificationRow { TenantId = notification.TenantId, TaskId = task.TaskId,
                        NotificationId = notification.NotificationId, RecipientSessionId = sourceSession.ToString("D"), Revision = 1,
                        Json = JsonSerializer.Serialize(notification) });
                }
            }
            var disabledPrincipal = before.SystemIdentities.Single(i => i.AgentInstanceId == instanceId).PrincipalId;
            var messages = await db.Set<AgentMessageRow>().Where(m => m.TenantId == next.Tenant.TenantId
                && (m.RecipientInstanceId == instanceId || m.SenderPrincipalId == disabledPrincipal)).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var messageRow in messages)
            {
                var message = TaskPersistence.Value<AgentMessage>(messageRow.Json);
                var receipt = TaskPersistence.Value<AgentMessageReceipt>(messageRow.ReceiptJson);
                if ((message.RecipientInstanceId == instanceId || message.SenderInstanceId == instanceId)
                    && receipt.State == AgentMessageDelivery.NotificationPending)
                    messageRow.ReceiptJson = JsonSerializer.Serialize(receipt with { State = AgentMessageDelivery.Undeliverable,
                        Reason = "agent_disabled", Revision = checked(receipt.Revision + 1), UpdatedAt = at });
            }
            var coreDeliveries = await db.CoreEventDeliveries.IgnoreQueryFilters().Where(d => d.TenantId == next.Tenant.TenantId
                && d.AgentInstanceId == id && (d.Status == (int)EventMatchStatus.Pending || d.Status == (int)EventMatchStatus.Matched
                    || d.Status == (int)EventMatchStatus.Coalesced || d.Status == (int)EventMatchStatus.AwaitingAdmission || d.Status == (int)EventMatchStatus.FilterError)).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var delivery in coreDeliveries)
            {
                if (delivery.Status == (int)EventMatchStatus.FilterError && !Retryable(delivery.DecisionJson)) continue;
                delivery.Status = (int)EventMatchStatus.PolicySkipped; delivery.Code = "agent_disabled";
            }
            var externalDeliveries = await db.ExternalEventDeliveries.IgnoreQueryFilters().Where(d => d.TenantId == next.Tenant.TenantId
                && d.AgentInstanceId == id && (d.Status == (int)ExternalEventDeliveryStatus.Pending || d.Status == (int)ExternalEventDeliveryStatus.Coalesced
                    || d.Status == (int)ExternalEventDeliveryStatus.AwaitingAdmission || d.Status == (int)ExternalEventDeliveryStatus.FilterError)).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var delivery in externalDeliveries)
            {
                if (delivery.Status == (int)ExternalEventDeliveryStatus.FilterError && !Retryable(delivery.DecisionJson)) continue;
                delivery.Status = (int)ExternalEventDeliveryStatus.Skipped; delivery.Code = "agent_disabled";
            }
            var buckets = await db.CoreEventBuckets.IgnoreQueryFilters().Where(b => b.TenantId == next.Tenant.TenantId && b.AgentInstanceId == id && !b.Flushed).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var bucket in buckets)
            {
                var settled = EventBucket(bucket) with { Flushed = true, CompletionCode = "agent_disabled" };
                bucket.Flushed = true; bucket.PayloadJson = JsonSerializer.Serialize(settled, CoreEventPersistence.Json);
            }
            var approvals = await db.Set<TenantApprovalRecord>().Where(r => r.TenantId == next.Tenant.TenantId
                && r.InstanceId == id).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var approval in approvals)
            {
                var request = approval.Domain();
                if (request.Status == TenantApprovalStatus.Pending)
                    db.Entry(approval).CurrentValues.SetValues(TenantApprovalRecord.From(request.Decide(
                        at >= request.ExpiresAt ? TenantApprovalStatus.Expired : TenantApprovalStatus.Cancelled, request.Revision, at)));
                var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.RequestId == approval.RequestId, ct).ConfigureAwait(false);
                if (grant is { Status: OperationGrantStatus.Available })
                    db.Entry(grant).CurrentValues.SetValues(grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) });
            }
        }
        if (lifecycle == AgentInstanceLifecycle.Active && old.Lifecycle == AgentInstanceLifecycle.Disabled)
        {
            var automations = await OwnedAutomations(db, next.Tenant.TenantId).Where(a => a.AgentInstanceId == id).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var automation in automations)
                StageAutomation(db, automation, AgentAvailabilityPolicy.SkipMissedSchedule(TriggerStoreMapping.ToRegistration(automation), at));
        }
        var updated = SqliteAgentInstanceStore.Map(row);
        foreach (var e in CoreEventPersistence.Instance(old, updated, AgentCore.Domain.Triggers.TriggerOwner.ForTenant(next.Tenant.TenantId, instanceId, before.SystemIdentities.Single(i => i.AgentInstanceId == instanceId).PrincipalId)))
            await TenantCoreEventProduction.StageAsync(db, e, ct).ConfigureAwait(false);
        await RequireAgentHumanAsync(db, next.Tenant.TenantId, audit.ActorPrincipalId, initiator, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
        var identity = next.SystemIdentities.Single(i => i.AgentInstanceId == instanceId);
        return new(next.Tenant.TenantId, identity.SystemIdentityId, identity.PrincipalId, updated, identity.ManagedKind);
    }

    private static bool Retryable(string? json)
    {
        if (json is null) return false;
        try { return JsonSerializer.Deserialize<EventFilterResult>(json, CoreEventPersistence.Json)?.Retryable == true; }
        catch (JsonException) { return false; } // Preserve unreadable historical failures; never turn them into work.
    }

    private sealed class AgentJoin
    {
        public required IamSystemIdentityRow Identity { get; init; }
        public required AgentInstanceRecord Instance { get; init; }
    }
    private static IQueryable<AgentJoin> OwnedAgents(AgentCoreDbContext db, Guid tenantId, bool includeRemoved = false, IQueryable<AgentInstanceRecord>? instances = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required.");
        return from identity in db.Set<IamSystemIdentityRow>().AsNoTracking()
            join instance in (instances ?? db.AgentInstances).AsNoTracking() on identity.AgentInstanceId equals instance.InstanceId
            where identity.TenantId == tenantId && identity.Kind == SystemIdentityKind.Agent && (includeRemoved || identity.Status != IdentityStatus.Removed)
            select new AgentJoin { Identity = identity, Instance = instance };
    }
    private static TenantAgent Owned(AgentJoin row) => new(row.Identity.TenantId!.Value, row.Identity.SystemIdentityId, row.Identity.PrincipalId, SqliteAgentInstanceStore.Map(row.Instance), row.Identity.ManagedKind);
    private static async Task ValidateTenantActorAsync(AgentCoreDbContext db, TenantSecurityState before, Guid actorId, CancellationToken ct)
    {
        var actor = before.Principals.SingleOrDefault(p => p.PrincipalId == actorId && p.Status == IdentityStatus.Active)
            ?? throw new AccessDeniedException("principal_unavailable");
        if (actor.Kind == PrincipalKind.User && !await db.Set<Account>().AnyAsync(a => a.AccountId == actor.AccountId && a.Status == IdentityStatus.Active, ct).ConfigureAwait(false))
            throw new AccessDeniedException("account_unavailable");
    }
    private async Task RequireAgentHumanAsync(AgentCoreDbContext db, Guid tenant, Guid actor, HumanSessionReference? initiator, CancellationToken ct)
    {
        if (initiator is null) return;
        if (initiator.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
        try { await RequireHumanSessionAsync(db, tenant, initiator, ct).ConfigureAwait(false); }
        catch (AccessDeniedException) { throw new AccessDeniedException("initiator_unavailable"); }
    }

}
