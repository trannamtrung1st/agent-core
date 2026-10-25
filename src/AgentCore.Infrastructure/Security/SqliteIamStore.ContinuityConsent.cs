using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    private async Task<bool> ContinuitySourceOwnersAvailable(AgentCoreDbContext db, TenantMemoryReader reader, PreparedContinuityRead read, CancellationToken ct)
    {
        var ids = TenantContinuityRules.Sessions(read).Select(s => s.SessionId.ToString("D")).ToArray();
        return await OwnedSessions(db, reader.Security.Tenant.TenantId).CountAsync(s => ids.Contains(s.SessionId) && s.AgentInstanceId == reader.Run.AgentInstanceId.ToString("D") && s.ExecutorPrincipalId == reader.Agent.PrincipalId, ct).ConfigureAwait(false) == ids.Length;
    }
    private async Task<bool> HistoricalReadConsented(AgentCoreDbContext db, TenantContinuityObservation observed, TenantHistoricalSource source, CancellationToken ct)
    {
        var reader = observed.Reader;
        var rows = await (from receipt in db.Set<TenantOperationReceiptRecord>().AsNoTracking()
            join approval in db.Set<TenantApprovalRecord>().AsNoTracking() on receipt.RequestId equals (Guid?)approval.RequestId
            join decision in db.Set<TenantApprovalDecision>().AsNoTracking() on approval.RequestId equals decision.RequestId
            where receipt.TenantId == reader.Security.Tenant.TenantId && approval.TenantId == receipt.TenantId && decision.TenantId == receipt.TenantId
                && receipt.SessionId == reader.Run.SessionId.ToString("D") && receipt.RootActivationId == reader.Run.Admission.Activation.RootActivationId.ToString("D")
                && receipt.Outcome == (int)TenantOperationOutcome.Completed && receipt.ReasonCode == TenantContinuityRules.Completed
            select new { approval.PayloadJson, decision.HumanPrincipalId }).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<TenantApprovalRequest>(row.PayloadJson)!;
            if (request.Operation.ContinuityRead is not { } read || !TenantContinuityRules.Sessions(read).Any(s => s.SessionId == source.SessionId && s.Revision == source.Revision
                && s.ThroughEntrySequence == source.ThroughEntrySequence && s.AfterEntrySequence == source.AfterEntrySequence && s.Limit == source.Limit && s.ObservationHash == source.ObservationHash)
                || !await ContinuitySourceOwnersAvailable(db, reader, read, ct).ConfigureAwait(false)) continue;
            var ids = read.Memory is null ? [] : TenantMemoryOperationRules.Sources(read.Memory).Select(s => s.MemoryId).ToArray();
            var memory = await db.Set<TenantLearnedMemoryRecord>().AsNoTracking().Where(m => m.TenantId == request.TenantId && ids.Contains(m.MemoryId))
                .Select(m => new TenantMemoryReference(m.TenantId, m.MemoryId, (MemoryScope)m.Scope, m.UserPrincipalId, m.AccountId, m.UserId,
                    Guid.Parse(m.AgentInstanceId), m.ExecutorPrincipalId, Guid.Parse(m.SessionId), m.ContentHash, (MemoryItemStatus)m.Status)).ToArrayAsync(ct).ConfigureAwait(false);
            if (memory.Length != ids.Length) continue;
            try
            {
                await ValidateTenantActorAsync(db, reader.Security, row.HumanPrincipalId, ct).ConfigureAwait(false);
                await RequireContinuityExperienceConsent(db, request, reader, row.HumanPrincipalId, ct).ConfigureAwait(false);
                await TenantContinuityReadRules.ConsentAsync(request, reader, memory, row.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false); return true;
            }
            catch (AccessDeniedException) { }
        }
        return false;
    }
    private async Task<TenantContinuityObservation> RequireContinuityOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var read = request.Operation.ContinuityRead ?? throw new SecurityConflictException("operation_mismatch");
        var observed = await ObserveContinuity(db, run, AgentCore.Application.Tools.TenantContinuityToolContract.Parse(read.ToolName, read.ArgumentsJson), read, null, ct).ConfigureAwait(false);
        await TenantContinuityReadRules.RawAsync(observed, new IamAuthorizer(this), ct).ConfigureAwait(false); return observed;
    }
    private async Task RequireContinuityExperienceConsent(AgentCoreDbContext db, TenantApprovalRequest request, TenantMemoryReader reader, Guid human, CancellationToken ct)
    {
        if (request.Operation.ContinuityRead?.Experience is null) return;
        var observed = await RequireContinuityOperation(db, request, reader.Run, ct).ConfigureAwait(false);
        await TenantContinuityReadRules.ReviewerAsync(request, observed, human, new IamAuthorizer(this), ct).ConfigureAwait(false);
        var facts = observed.Experience ?? throw new AccessDeniedException("experience_source_changed");
        await TenantExperienceConsentRules.FactsAsync(request, reader, facts.Settings, facts.Items.Select(i => i.Reference()).ToArray(), human, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }

}
