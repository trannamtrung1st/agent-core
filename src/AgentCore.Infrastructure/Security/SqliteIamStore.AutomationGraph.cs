using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask StageTenantBackgroundGraph(AgentCoreDbContext db, TenantSessionRoot root, SessionSnapshot snapshot, AgentRun child, CancellationToken ct)
    {
        var all = (await OwnedRuns(db, root.TenantId).AsNoTracking().Where(r => r.AgentInstanceId == root.AgentInstanceId.ToString("D"))
            .ToArrayAsync(ct).ConfigureAwait(false)).Select(AgentRunStoreMapping.ToDomain);
        AgentRunStoreMapping.ValidateBackgroundBudget(child, all);
        db.Sessions.Add(SqliteMemoryStore.ToRecord(snapshot)); db.Add(TenantSessionRecord.Row(root));
        db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(snapshot, child)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(child));
        db.ActivationSourceEntries.Add(new() { ActivationId = child.ActivationId.ToString("D"), SessionId = child.SessionId.ToString("D"), EntryId = snapshot.Entries[0].EntryId.ToString("D"), Ordinal = 0 });
    }
}
