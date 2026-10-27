using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask RequireAgentMessageExecutionSourcesAsync(AgentRun wake, CancellationToken ct = default)
    {
        var work = wake.Admission.AgentMessage ?? throw new AccessDeniedException("message_wake_unavailable"); var tenant = wake.Owner.TenantId!.Value;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var saved = await FindAgentMessageSource(db, tenant, work.MessageId, ct) ?? throw new AccessDeniedException("message_source_unavailable");
        if (saved.Receipt.WakeSessionId != wake.SessionId || saved.Receipt.WakeRootActivationId != work.WakeRootActivationId || saved.Receipt.WakeHops != work.WakeHops)
            throw new AccessDeniedException("message_wake_unavailable");
        var sourceRun = await FindTenantRunAsync(tenant, saved.Message.SourceRunId!.Value, ct) ?? throw new AccessDeniedException("message_source_unavailable");
        var root = await TenantExecutionLineage.RootAsync(sourceRun, this, ct);
        var senderRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == saved.Message.SenderInstanceId!.Value.ToString("D"), ct) ?? throw new AccessDeniedException("message_source_unavailable");
        var recipientRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == saved.Message.RecipientInstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("message_recipient_unavailable");
        var sender = Owned(senderRow); var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        await AgentMessageWakeRules.AuthorizeAsync(new(saved.Thread, saved.Message, saved.Receipt, sourceRun, root.Admission.AgentMessage), sender, Owned(recipientRow), state,
            AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, sender, ct), sender.Instance.SettingsOverrides), new(this), ct, pending: false);
    }
    private async Task<AgentMessageResult?> FindAgentMessageSource(AgentCoreDbContext db, Guid tenant, Guid message, CancellationToken ct)
    {
        var row = await db.Set<AgentMessageRow>().AsNoTracking().SingleOrDefaultAsync(m => m.TenantId == tenant && m.MessageId == message, ct); if (row is null) return null;
        var value = TaskPersistence.Value<AgentMessage>(row.Json);
        if (value.SourceSessionId is not { } source || await ReadSandboxSession(db, tenant, source, ct) is not { } session || SessionRetention.IsDue(session.Snapshot, TaskNow)) return null;
        var thread = await db.Set<AgentThreadRow>().AsNoTracking().SingleAsync(t => t.TenantId == tenant && t.ThreadId == row.ThreadId, ct);
        return new(TaskPersistence.Value<AgentThread>(thread.Json), value, TaskPersistence.Value<AgentMessageReceipt>(row.ReceiptJson), 0);
    }
    public async ValueTask<AgentMessageResult?> FindAgentMessageSourceAsync(Guid tenant, Guid message, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return await FindAgentMessageSource(db, tenant, message, ct); }
    private int messageWakeOffset;
    public async ValueTask<IReadOnlyList<AgentMessageWakeSource>> ListAgentMessageWakesAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var candidates = db.Set<AgentMessageRow>().FromSqlRaw("""
            SELECT * FROM TenantAgentMessages WHERE json_extract(ReceiptJson,'$.State')=0
            """).AsNoTracking().OrderBy(m => m.MessageId);
        var count = await candidates.CountAsync(ct); var take = Math.Clamp(limit, 1, 32);
        var offset = count == 0 ? 0 : (int)(unchecked((uint)Interlocked.Add(ref messageWakeOffset, take) - (uint)take) % (uint)count);
        var rows = await candidates.Skip(offset).Take(take).ToArrayAsync(ct);
        if (rows.Length < take && offset > 0) rows = rows.Concat(await candidates.Take(Math.Min(offset, take - rows.Length)).ToArrayAsync(ct)).ToArray();
        var result = new List<AgentMessageWakeSource>();
        foreach (var row in rows)
        {
            var message = TaskPersistence.Value<AgentMessage>(row.Json);
            var source = await OwnedRuns(db, row.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == message.SourceRunId!.Value.ToString("D"), ct);
            var thread = await db.Set<AgentThreadRow>().AsNoTracking().SingleAsync(t => t.TenantId == row.TenantId && t.ThreadId == row.ThreadId, ct);
            var receipt = TaskPersistence.Value<AgentMessageReceipt>(row.ReceiptJson);
            if (source is null || message.SourceSessionId is not { } sessionId || await ReadSandboxSession(db, row.TenantId, sessionId, ct) is not { } session
                || SessionRetention.IsDue(session.Snapshot, TaskNow))
            {
                await WithholdAgentMessageWakeAsync(row.TenantId, row.MessageId, receipt.Revision, "message_source_unavailable", ct);
                continue;
            }
            var sourceRun = AgentRunStoreMapping.ToDomain(source);
            try
            {
                var root = await TenantExecutionLineage.RootAsync(sourceRun, this, ct);
                result.Add(new(TaskPersistence.Value<AgentThread>(thread.Json), message, receipt, sourceRun, root.Admission.AgentMessage));
            }
            catch (AccessDeniedException)
            { await WithholdAgentMessageWakeAsync(row.TenantId, row.MessageId, receipt.Revision, "message_source_unavailable", ct); }
        }
        return result;
    }
    public async ValueTask WithholdAgentMessageWakeAsync(Guid tenant, Guid message, long revision, string reason, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<AgentMessageRow>().SingleOrDefaultAsync(m => m.TenantId == tenant && m.MessageId == message, ct); if (row is null) return;
        var receipt = TaskPersistence.Value<AgentMessageReceipt>(row.ReceiptJson);
        if (receipt.State != AgentMessageDelivery.NotificationPending || receipt.Revision != revision) return;
        var withheld = receipt with { Revision = revision + 1, State = AgentMessageDelivery.Undeliverable, Reason = TaskLimits.Text(reason, 160, "Wake reason"), UpdatedAt = TaskNow };
        row.ReceiptJson = JsonSerializer.Serialize(withheld);
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct);
        AgentCore.Application.Observability.TaskTelemetry.Message(AgentCore.Application.Observability.AgentMessageEvent.WakeWithheld, TaskPersistence.Value<AgentMessage>(row.Json), withheld);
    }
    public async ValueTask<bool> AdmitAgentMessageWakeAsync(AgentMessageWakeAdmission admission, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var message = admission.Source.Message; var tenant = message.TenantId;
        var saved = await FindAgentMessageSource(db, tenant, message.MessageId, ct) ?? throw new AccessDeniedException("message_source_unavailable");
        if (saved.Receipt.WakeRunId is not null) return false;
        if (saved.Receipt.Revision != admission.Source.Receipt.Revision || saved.Message != message || saved.Thread != admission.Source.Thread) throw new SecurityConflictException("message_wake_changed");
        var sourceRow = await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == message.SourceRunId!.Value.ToString("D"), ct) ?? throw new AccessDeniedException("message_source_unavailable");
        var sourceRun = AgentRunStoreMapping.ToDomain(sourceRow);
        var root = await TenantExecutionLineage.RootAsync(sourceRun, this, ct);
        if (admission.Source.WakeAncestor != root.Admission.AgentMessage) throw new SecurityConflictException("message_wake_changed");
        var source = new AgentMessageWakeSource(saved.Thread, saved.Message, saved.Receipt, sourceRun, root.Admission.AgentMessage);
        var senderRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == message.SenderInstanceId!.Value.ToString("D"), ct) ?? throw new AccessDeniedException("message_source_unavailable");
        var recipientRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == message.RecipientInstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("message_recipient_unavailable");
        var recipient = Owned(recipientRow); var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        await AgentMessageWakeRules.AuthorizeAsync(source, Owned(senderRow), recipient, state, AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, Owned(senderRow), ct), Owned(senderRow).Instance.SettingsOverrides), new(this), ct);
        var pair = saved.Thread;
        var pairThreads = db.Set<AgentThreadRow>().Where(t => t.TenantId == tenant && t.TaskId == null
            && t.FirstInstanceId == pair.FirstInstanceId && t.SecondInstanceId == pair.SecondInstanceId).Select(t => t.ThreadId);
        var prior = await db.Set<AgentMessageRow>().AsNoTracking().Where(m => m.TenantId == tenant && pairThreads.Contains(m.ThreadId)).ToArrayAsync(ct);
        var wakeIds = prior.Select(m => TaskPersistence.Value<AgentMessageReceipt>(m.ReceiptJson).WakeRunId).OfType<Guid>().Select(id => id.ToString("D")).ToArray();
        var previous = await db.AgentRuns.AsNoTracking().Where(r => r.TenantId == tenant && wakeIds.Contains(r.AgentRunId)).ToArrayAsync(ct);
        if (previous.Select(AgentRunStoreMapping.ToDomain).Any(r => !r.IsTerminal)) throw new SecurityConflictException("message_thread_busy");
        if (previous.Select(AgentRunStoreMapping.ToDomain).Count(r => r.CreatedAtUtc > TaskNow.AddHours(-1)) >= 8) throw new AccessDeniedException("message_pair_rate_limit");
        AgentMessageWakeRules.Shape(admission, recipient, TaskNow); var run = admission.Run;
        await RequireSpaceExecutionSource(db, run, state, new(this), ct, admission: true);
        AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(run.PinnedModel,
            await db.Set<TenantModelSettingsRecord>().Where(m => m.TenantId == tenant).Select(m => (long?)m.Revision).SingleOrDefaultAsync(ct) ?? 0);
        StageTaskGraph(db, new(tenant, run.SessionId, recipient.Instance.InstanceId, recipient.PrincipalId,
            sourceRun.Admission.ExecutionContext!.OriginalInitiatorPrincipalId ?? recipient.PrincipalId, run.CreatedAtUtc), admission.Session, run);
        var receipt = saved.Receipt with { Revision = saved.Receipt.Revision + 1, State = AgentMessageDelivery.ActivationAdmitted, UpdatedAt = TaskNow,
            WakeRunId = run.AgentRunId, WakeSessionId = run.SessionId, WakeRootActivationId = run.Admission.AgentMessage!.WakeRootActivationId, WakeHops = run.Admission.AgentMessage.WakeHops }; receipt.Validate();
        var row = await db.Set<AgentMessageRow>().SingleAsync(m => m.TenantId == tenant && m.MessageId == message.MessageId, ct); row.ReceiptJson = JsonSerializer.Serialize(receipt);
        await BumpTaskSecurity(db, state, ct); db.Add(new AuthorizationAudit(admission.AuditId, tenant, recipient.PrincipalId, "agent-messages.wake", message.MessageId.ToString("D"), "allowed", "recipient_own_wake_admitted", TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct);
        AgentCore.Application.Observability.TaskTelemetry.Message(AgentCore.Application.Observability.AgentMessageEvent.WakeAdmitted, message, receipt);
        AgentCore.Application.Observability.TaskTelemetry.DeliveryLag("message", message.CreatedAt, run.CreatedAtUtc); return true;
    }
}
