using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantRunAdmissionResult?> FindTenantAcceptedHumanInputAsync(TenantSessionRoot owner, Guid principal, Guid message,
        string text, IReadOnlyList<Guid> attachments, TenantInputInterruption? interruption, AgentCore.Domain.Conversation.SessionMode mode, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var root = await OwnedSessions(db, owner.TenantId).AsNoTracking().SingleOrDefaultAsync(o => o.SessionId == owner.SessionId.ToString("D"), ct).ConfigureAwait(false);
        if (root?.Domain() != owner) return null;
        var key = $"human:{principal:D}:{message:D}";
        var row = await OwnedRuns(db, owner.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.SessionId == root.SessionId
            && db.Activations.Any(a => a.ActivationId == r.ActivationId && a.TenantId == owner.TenantId && a.DedupeKey == key), ct).ConfigureAwait(false);
        if (row is null) return null;
        var run = AgentRunStoreMapping.ToDomain(row);
        if (run.Owner.ExecutorPrincipalId != owner.ExecutorPrincipalId || run.AgentInstanceId != owner.AgentInstanceId
            || run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId != principal) throw new AccessDeniedException("resource_unavailable");
        var entryId = run.Admission.Activation.SourceEntryIds.Single().ToString("D");
        var entry = SqliteMemoryStore.ToEntry(await db.Entries.AsNoTracking().SingleAsync(e => e.SessionId == root.SessionId && e.EntryId == entryId, ct).ConfigureAwait(false));
        TenantInputReplayRules.Match(run, entry, text, attachments, interruption, mode);
        var revision = await db.Sessions.Where(s => s.SessionId == root.SessionId).Select(s => s.Revision).SingleAsync(ct).ConfigureAwait(false);
        return new(false, run, entry.EntryId, revision) { EntrySequence = entry.Sequence };
    }
}
