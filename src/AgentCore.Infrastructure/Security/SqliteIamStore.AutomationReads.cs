using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore : ITenantAutomationReadStore
{
    public async ValueTask<TenantAutomationReadObservation> ObserveTenantAutomationReadAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        PreparedAutomationRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("automation_read_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        return await ObserveAutomationRead(db, run, tool, arguments, expected, inspectionHuman, ct).ConfigureAwait(false);
    }
    private async Task<TenantAutomationReadObservation> ObserveAutomationRead(AgentCoreDbContext db, AgentRun run, string tool, string arguments, PreparedAutomationRead? expected, Guid? human, CancellationToken ct)
    {
        var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); var args = TenantAutomationReadContract.Parse(tool, arguments); var iam = new IamAuthorizer(this);
        TenantAutomationReadRules.Eligible(reader, tool);
        if (human is not null) { if (expected is null) throw new AccessDeniedException("approval_unavailable"); await ValidateTenantActorAsync(db, reader.Security, human.Value, ct).ConfigureAwait(false); }
        if (expected is not null)
        {
            if (expected.ToolName != tool || expected.ArgumentsJson != args.Json) throw new AccessDeniedException("automation_source_changed");
            await TenantAutomationReadRules.ValidateCurrentAsync(reader, expected,
                async (id, token) => { var row = await OwnedAutomations(db, reader.Security.Tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.AutomationId == id.ToString("D"), token).ConfigureAwait(false); return row is null ? null : TriggerStoreMapping.ToRegistration(row); },
                (kind, id, trigger, token) => new(TenantEvent(db, reader.Security.Tenant.TenantId, kind, id, trigger, token)), iam, ct, human, async (source, current, token) => {
                    var session = reader.Run.SessionId.ToString("D"); var root = reader.Run.Admission.Activation.RootActivationId.ToString("D");
                    var receipts = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().Where(r => r.TenantId == reader.Security.Tenant.TenantId && r.SessionId == session && r.RootActivationId == root && r.AutomationMutationJson != null).Take(1025).ToArrayAsync(token).ConfigureAwait(false);
                    return TenantAutomationMutationEvidence.Explains(reader, source, current, receipts.Select(r => r.Domain()));
                }).ConfigureAwait(false);
            return new(reader, expected);
        }
        var allowed = await TenantAutomationReadRules.VisibleAsync(reader, iam, ct).ConfigureAwait(false);
        var query = OwnedAutomations(db, reader.Security.Tenant.TenantId).AsNoTracking().Where(a => a.AgentInstanceId == run.AgentInstanceId.ToString("D") && a.ExecutorPrincipalId == reader.Agent.PrincipalId);
        if (allowed is not null) { var ids = allowed.Select(id => id.ToString("D")).ToArray(); query = query.Where(a => ids.Contains(a.AutomationId)); }
        if (args.AutomationId is { } selected) query = query.Where(a => a.AutomationId == selected.ToString("D"));
        if (args.Status is { } status) { var state = status == "suspended" ? AutomationStatus.SuspendedPolicy : Enum.Parse<AutomationStatus>(status, true); query = query.Where(a => a.Status == (int)state); }
        var total = await query.CountAsync(ct).ConfigureAwait(false); var rows = await query.OrderBy(a => a.AutomationId).Skip(args.AutomationId is null ? args.Page * args.PageSize : 0).Take(args.AutomationId is null ? args.PageSize : 1).ToArrayAsync(ct).ConfigureAwait(false);
        if (args.AutomationId is not null && rows.Length != 1) throw new AccessDeniedException("automation_read_required");
        var coverage = args.AutomationId is { } id ? await ListTenantEventCoverageAsync(reader.Security.Tenant.TenantId, run.AgentInstanceId, id,
            await TenantAutomationReadRules.VisibilityAsync(reader, iam, ct).ConfigureAwait(false), args.CoverageCursor, 4, ct).ConfigureAwait(false) : null;
        var deliveries = new List<TenantEventDelivery>();
        foreach (var source in coverage?.Items ?? [])
        {
            var kind = await db.CoreEvents.IgnoreQueryFilters().AnyAsync(e => e.TenantId == reader.Security.Tenant.TenantId && e.EventId == source.Source.EventId.ToString("D"), ct).ConfigureAwait(false) ? TriggerSourceKind.CoreEvent : TriggerSourceKind.ApplicationEvent;
            deliveries.Add(await TenantEvent(db, reader.Security.Tenant.TenantId, kind, source.Source.EventId, source.TriggerId!.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("automation_source_changed"));
        }
        var value = TenantAutomationReadRules.Build(reader, args, rows.Select(TriggerStoreMapping.ToRegistration).ToArray(), total, coverage, deliveries);
        await TenantAutomationReadRules.RawAsync(reader, value, iam, ct).ConfigureAwait(false); return new(reader, value);
    }
}
