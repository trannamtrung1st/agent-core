using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask<AgentRun?> CompletionRun(AgentCoreDbContext db, Guid tenant, Guid id, CancellationToken ct)
    {
        var row = await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == id.ToString("D"), ct).ConfigureAwait(false);
        return row is null ? null : AgentRunStoreMapping.ToDomain(row);
    }
    private async ValueTask<TenantSession?> CompletionSession(AgentCoreDbContext db, Guid tenant, Guid id, CancellationToken ct)
    {
        var row = await OwnedSessions(db, tenant).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == id.ToString("D"), ct).ConfigureAwait(false);
        return row is null ? null : await SessionView(db, row, ct).ConfigureAwait(false);
    }
    private async Task RequireTenantOutputSources(AgentCoreDbContext db, AgentRun parent, CancellationToken ct)
    {
        await RequireSpaceResourceReadSources(db, parent, ct).ConfigureAwait(false);
        await RequireContextReadSources(db, parent, ct).ConfigureAwait(false);
        await RequirePublicWebSources(db, parent, ct).ConfigureAwait(false);
        await RequireHttpSources(db, parent, ct).ConfigureAwait(false);
        await RequireStoredProfileSource(db, parent, ct).ConfigureAwait(false);
        await RequireStoredMemorySources(db, parent, ct).ConfigureAwait(false);
        await RequireTenantEventRead(db, parent, ct).ConfigureAwait(false);
        await RequireCredentialReadSources(db, parent, ct).ConfigureAwait(false);
        await RequireAccountReadSources(db, parent, ct).ConfigureAwait(false);
        await RequireAccountActionSources(db, parent, ct).ConfigureAwait(false);
        var tenant = parent.Owner.TenantId!.Value; var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
        if (parent.Admission.TaskReport is not null)
            await RequireTaskReportSources(db, parent, ct);
        else if (parent.Admission.OutputContract == AgentRunOutputContract.CompletionReport)
        {
            var source = await TenantReportReadRules.SourceAsync(parent, (id, token) => CompletionRun(db, tenant, id, token),
                async (id, token) => await CompletionSource(db, parent.Owner, id, token).ConfigureAwait(false), ct).ConfigureAwait(false);
            await RequireTenantReportRead(db, state, source, ct).ConfigureAwait(false);
            await RequireTenantReportDelivery(db, state, source, parent, ct).ConfigureAwait(false);
        }
    }
}
