using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantAutomationStore
{
    private static IQueryable<AutomationRecord> OwnedAutomations(AgentCoreDbContext db, Guid tenant) => db.Automations.IgnoreQueryFilters()
        .Where(a => a.TenantId == tenant && db.Set<IamSystemIdentityRow>().Any(i => i.TenantId == tenant && i.AgentInstanceId == a.AgentInstanceId && i.PrincipalId == a.ExecutorPrincipalId));
    public async ValueTask<Automation?> FindTenantAutomationAsync(Guid tenant, Guid automation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var row = await OwnedAutomations(db, tenant).AsNoTracking().SingleOrDefaultAsync(a => a.AutomationId == automation.ToString("D"), ct);
        return row is null ? null : TriggerStoreMapping.ToRegistration(row);
    }
    public async ValueTask<TriggerOccurrence?> FindTenantOccurrenceAsync(Guid tenant, Guid occurrence, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.TriggerOccurrences.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(o => o.TenantId == tenant && o.OccurrenceId == occurrence.ToString("D"), ct);
        return row is null ? null : TriggerStoreMapping.ToOccurrence(row);
    }
    public async ValueTask<TenantAutomationPage> ListTenantAutomationsAsync(Guid tenant, Guid instance, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var query = OwnedAutomations(db, tenant).AsNoTracking().Where(a => a.AgentInstanceId == instance.ToString("D"));
        if (allowed is not null) { var ids = allowed.Select(id => id.ToString("D")).ToArray(); query = query.Where(a => ids.Contains(a.AutomationId)); }
        var total = await query.CountAsync(ct); var rows = await query.OrderBy(a => a.AutomationId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(rows.Select(TriggerStoreMapping.ToRegistration).ToArray(), total, page, pageSize);
    }
    public async ValueTask<IReadOnlyList<Automation>> ListDueTenantAutomationsAsync(DateTimeOffset at, int limit, CancellationToken ct = default, string? afterId = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Automations.IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantId != null && a.Status == (int)AutomationStatus.Active
            && a.NextOccurrenceAtUtc <= at.ToUnixTimeMilliseconds() && db.Set<Tenant>().Any(t => t.TenantId == a.TenantId && t.Status == IdentityStatus.Active))
            .Where(a => afterId == null || a.AutomationId.CompareTo(afterId) > 0).OrderBy(a => a.AutomationId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }
    public async ValueTask<IReadOnlyList<Automation>> ListFutureTenantAutomationsAsync(Guid tenant, Guid instance, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await OwnedAutomations(db, tenant).AsNoTracking().Where(a => a.AgentInstanceId == instance.ToString("D")
            && (a.Status == (int)AutomationStatus.Active || a.Status == (int)AutomationStatus.SuspendedPolicy)).OrderBy(a => a.AutomationId).Take(Math.Clamp(limit, 1, 256)).ToArrayAsync(ct);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }
    public ValueTask<bool> TrySaveTenantAutomationAsync(TenantSecurityState next, long expectedSecurityRevision, Automation proposed, long expectedAutomationRevision,
        long expectedInstanceRevision, HumanSessionReference human, string permission, AuthorizationAudit audit, CancellationToken ct = default) => WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
    {
        await RequireHumanSessionAsync(db, next.Tenant.TenantId, human, ct);
        var row = await OwnedAutomations(db, next.Tenant.TenantId).SingleOrDefaultAsync(a => a.AutomationId == proposed.AutomationId.ToString("D"), ct);
        var current = row is null ? null : TriggerStoreMapping.ToRegistration(row);
        if ((current?.Revision ?? 0) != expectedAutomationRevision) return false;
        var agentRow = await OwnedAgents(db, next.Tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == proposed.Owner.AgentInstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = Owned(agentRow); if (agent.Instance.Revision != expectedInstanceRevision) return false;
        TenantAutomationRules.Owner(proposed, agent);
            if (proposed.Status == AutomationStatus.Active && agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_unavailable"); var before = (await ReadTenantAsync(db, next.Tenant.TenantId, ct))!; TenantSessionRules.RequireExecutor(before, agent);
        var policy = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides);
        if (current?.Status is AutomationStatus.Completed or AutomationStatus.Expired && proposed.Status != AutomationStatus.Cancelled) throw new AccessDeniedException("automation_terminal");
            TenantAutomationPresetRules.ValidateNew(current, proposed);
            AutomationRules.ValidateSave(current, proposed, expectedAutomationRevision); TenantAutomationRules.Configuration(proposed, policy, audit.At);
        await RequireAutomationHuman(before, human.PrincipalId, agent, proposed.AutomationId, permission, current is null, ct);
        if (permission is "automations.create" or "automations.update") await TenantAutomationRules.HumanSourcesAsync(before, human.PrincipalId, proposed, new IamAuthorizer(this), ct);
        if (permission is "automations.create" or "automations.update")
        {
            var target = proposed.CompletionDelivery.SessionId is { } destination
                ? await OwnedSessions(db, before.Tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == destination.ToString("D"), ct) : null;
            await TenantCompletionDestinationRules.AuthoringAsync(before, human.PrincipalId, proposed,
                target is null ? null : await SessionView(db, target, ct), new IamAuthorizer(this), ct);
        }
        if (permission is "automations.create" or "automations.update")
            await RequireAutomationConversation(db, before, proposed, human.PrincipalId, null, ct);
        ValidateAutomationTransaction(next, expectedSecurityRevision, proposed, human.PrincipalId, permission, audit);
        if (proposed.Status == AutomationStatus.Active && current?.Status != AutomationStatus.Active
            && await OwnedAutomations(db, next.Tenant.TenantId).CountAsync(a => a.AgentInstanceId == agent.Instance.InstanceId.ToString("D") && a.Status == (int)AutomationStatus.Active, ct) >= (policy.TriggerPolicy?.MaxActiveRegistrations ?? 0))
            throw new AccessDeniedException("automation_limit_reached");
        StageAutomation(db, row, proposed); return true;
    }, ct);
    private static void StageAutomation(AgentCoreDbContext db, AutomationRecord? row, Automation proposed)
    {
        var replacement = TriggerStoreMapping.ToRecord(proposed);
        if (row is null) { db.Automations.Add(replacement); return; }
        db.Entry(row).CurrentValues.SetValues(replacement);
        foreach (var old in row.Triggers.ToArray())
        {
            var child = replacement.Triggers.SingleOrDefault(t => t.TriggerId == old.TriggerId);
            if (child is null) { db.Remove(old); row.Triggers.Remove(old); }
            else db.Entry(old).CurrentValues.SetValues(child);
        }
        foreach (var child in replacement.Triggers.Where(t => row.Triggers.All(old => old.TriggerId != t.TriggerId))) row.Triggers.Add(child);
    }
    public ValueTask<bool> TryAdvanceTenantAutomationAsync(TenantSecurityState next, long expectedSecurityRevision, Automation current, Automation advanced, AuthorizationAudit audit, CancellationToken ct = default)
        => WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
    {
        var row = await OwnedAutomations(db, next.Tenant.TenantId).SingleOrDefaultAsync(a => a.AutomationId == current.AutomationId.ToString("D"), ct);
        if (row is null || !TenantAutomationRules.Same(TriggerStoreMapping.ToRegistration(row), current)) return false;
        var agentRow = await OwnedAgents(db, next.Tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == current.Owner.AgentInstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = Owned(agentRow); var before = (await ReadTenantAsync(db, next.Tenant.TenantId, ct))!;
        TenantAutomationRules.Owner(current, agent); TenantSessionRules.RequireExecutor(before, agent); TenantAutomationRules.TechnicalAdvance(current, advanced, audit.At);
        ValidateAutomationTransaction(next, expectedSecurityRevision, current, agent.PrincipalId, "automations.schedule.advance", audit);
        StageAutomation(db, row, advanced); return true;
    }, ct);
    private ValueTask RequireAutomationHuman(TenantSecurityState state, Guid human, TenantAgent agent, Guid id, string permission, bool create, CancellationToken ct)
        => TenantAutomationRules.HumanAsync(state, human, agent, id, permission, create, new IamAuthorizer(this), ct);
    private static void ValidateAutomationTransaction(TenantSecurityState next, long revision, Automation value, Guid actor, string action, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != value.Owner.TenantId || next.Tenant.Revision != revision + 1 || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != actor
            || audit.Action != action || audit.Target != value.AutomationId.ToString("D") || audit.Decision != "allowed") throw new ArgumentException("Invalid Automation transaction.");
    }
    public async ValueTask<TenantAutomationAdmissionResult?> TryAdmitTenantAutomationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAutomationAdmission admission, HumanSessionReference? human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct);
        if (tenant?.Revision != expectedSecurityRevision) return null;
        if (human is not null) await RequireHumanSessionAsync(db, tenant.TenantId, human, ct);
        var prior = await db.TriggerOccurrences.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == tenant.TenantId && o.OccurrenceId == admission.Occurrence.OccurrenceId.ToString("D"), ct);
        if (prior is not null) return TenantAutomationRules.Existing(TriggerStoreMapping.ToOccurrence(prior));
        var row = await OwnedAutomations(db, tenant.TenantId).SingleOrDefaultAsync(a => a.AutomationId == admission.Current.AutomationId.ToString("D"), ct);
        var current = row is null ? null : TriggerStoreMapping.ToRegistration(row);
        if (current is null || !TenantAutomationRules.Same(current, admission.Current)) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct))!;
        var agentRow = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == current.Owner.AgentInstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = Owned(agentRow); var policy = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides);
            TenantAutomationRules.Admission(admission, agent, human?.PrincipalId); await TenantAutomationRules.AdmissionAuthority(before, current, agent, policy, new IamAuthorizer(this), ct, human?.PrincipalId, admission.Occurrence.SourceKind, admission.EventSource, TenantEventAdmissionRules.EmailAccounts(admission));
        if (human is not null) await RequireAutomationHuman(before, human.PrincipalId, agent, current.AutomationId, "automations.run", false, ct);
        ValidateAutomationTransaction(next, expectedSecurityRevision, current, agent.PrincipalId, "automations.occurrence.admit", audit);
        if (await TenantAutomationBusy(db, tenant.TenantId, admission.Current.AutomationId, ct)) return null;
        await RequireAutomationConversationAdmission(db, before, admission, human?.PrincipalId, null, false, ct);
        await RequireTenantEventAdmission(db, admission, null, false, ct);
        await StageTenantAutomationGraph(db, admission, ct);
        StageAutomation(db, row, admission.Next); db.TriggerOccurrences.Add(TriggerStoreMapping.ToRecord(admission.Occurrence));
        await SettleTenantEventSource(db, admission.EventSource, admission.Occurrence.OccurrenceId, false, null, ct);
        db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct); db.Add(audit);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, admission.Occurrence, admission.Run.SessionId, admission.Run.AgentRunId); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }
}
