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
        TenantSandboxControlRules.RequireCurrentSelection(lease, agent, SandboxValue(latest));
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
    private async ValueTask<(SandboxLease Lease, SandboxExecution? Execution)> RequireSandboxControlOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var value = request.Operation.SandboxControl!; var tenant = request.TenantId;
        var session = await ReadSandboxSession(db, tenant, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == value.SessionId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var receipt = value.ExecutionId is { } executionId ? await db.Set<SandboxExecutionRow>().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == row.SessionId && e.Id == executionId.ToString("D"), ct).ConfigureAwait(false) : null;
        var lease = SandboxRuntimePersistence.Value(row); var execution = receipt is null ? null : SandboxRuntimePersistence.Value(receipt);
        AgentDefinition? baseline;
        if (Guid.TryParse(agent.Instance.DefinitionId, out var definition))
        {
            var publication = await Publication(db, tenant, definition, agent.Instance.ActiveVersion).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
            baseline = publication is null ? null : AgentDefinitionAdminMapping.MapPublication(publication).Payload;
        }
        else baseline = builtIns is null ? null : await builtIns.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        TenantSandboxControlOperationRules.Require(value, run, agent, baseline, session, lease, execution);
        if (request.Snapshot?.SandboxControl is not { } snapshot || snapshot.ProfileName != lease.Profile.Name || snapshot.ImageReference != lease.Profile.ImageReference)
            throw new SecurityConflictException("sandbox_operation_witness_changed");
        var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
        await TenantSandboxControlOperationRules.RequirePermissionsAsync(state, session.Owner, value.ProfileId, agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        return (lease, execution);
    }

}
