using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireSandboxOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct, SandboxExecution? reserved = null)
    {
        var value = request.Operation.SandboxCommand!; var tenant = request.TenantId;
        var source = await OwnedSandboxProfiles(db, tenant).AsNoTracking().SingleOrDefaultAsync(p => p.Id == value.ProfileId && p.Revision == value.ProfileRevision, ct).ConfigureAwait(false);
        var latest = await OwnedSandboxProfiles(db, tenant).AsNoTracking().Where(p => p.Id == value.ProfileId).OrderByDescending(p => p.Revision).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var session = await ReadSandboxSession(db, tenant, value.SessionId, ct).ConfigureAwait(false);
        if (source is null || session is null || latest is null || !SandboxValue(latest).Enabled) throw new AccessDeniedException("resource_unavailable");
        AgentDefinition? baseline;
        if (Guid.TryParse(agent.Instance.DefinitionId, out var definition))
        {
            var publication = await Publication(db, tenant, definition, agent.Instance.ActiveVersion).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
            baseline = publication is null ? null : AgentDefinitionAdminMapping.MapPublication(publication).Payload;
        }
        else baseline = builtIns is null ? null : await builtIns.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        var row = await db.Set<SandboxLeaseRow>().AsNoTracking().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == value.SessionId.ToString("D"), ct).ConfigureAwait(false);
        var lease = row is null ? null : SandboxRuntimePersistence.Value(row);
        TenantSandboxOperationRules.Require(value, run, agent, session, baseline, SandboxValue(source), lease, ApprovalNow, reserved);
        var pinned = TenantSandboxOperationRules.Profile(run, value.ProfileId);
        if (request.Snapshot != TenantSandboxOperationRules.Snapshot(agent, pinned, lease, reserved?.InitializesGeneration)) throw new SecurityConflictException("sandbox_operation_witness_changed");
        if (pinned.RegistryCredentialId is { } credential)
        {
            var saved = await OwnedCredentials(db, tenant).SingleOrDefaultAsync(c => c.CredentialId == credential.ToString("D"), ct).ConfigureAwait(false);
            TenantSandboxProfileRules.RequireCredential(pinned, saved is null ? null : SqliteCredentialStore.Map(saved), ApprovalNow);
        }
        var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
        await TenantSandboxRuntimeRules.RequirePermissionsAsync(state, run, pinned, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
