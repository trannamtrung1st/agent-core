using System.Text.Json;
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
    public async ValueTask<SandboxLease?> TryRequestTenantSandboxProfileSwitchAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxProfileSwitchPreparation selection, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        var tenant = next.Tenant.TenantId; var c = selection.Change;
        TenantSandboxControlRules.SelectionAudit(tenant, c, human, audit); SandboxLease? requested = null; bool written;
        try
        {
            written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
            {
                await RequireAgentHumanAsync(db, tenant, human.PrincipalId, human, ct).ConfigureAwait(false);
                var session = await ReadSandboxSession(db, tenant, c.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == c.SessionId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                var lease = SandboxRuntimePersistence.Value(row); var executionId = (lease.ActiveExecutionId ?? lease.LastExecutionId)?.ToString("D");
                var execution = executionId is null ? null : await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == row.SessionId && e.Id == executionId, ct).ConfigureAwait(false);
                var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
                foreach (var profile in new[] { lease.Profile.Id, c.ProfileId }.Distinct(StringComparer.Ordinal))
                    await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, profile, human, new IamAuthorizer(this), ct).ConfigureAwait(false);
                var parent = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == session.Owner.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                var agent = Owned(parent); TenantSessionRules.RequireExecutor(state, agent);
                TenantSandboxControlRules.RequireSelection(selection, session, lease, execution is null ? null : SandboxRuntimePersistence.Value(execution), agent);
                AgentDefinition? baseline;
                if (Guid.TryParse(agent.Instance.DefinitionId, out var definition))
                {
                    var publication = await Publication(db, tenant, definition, agent.Instance.ActiveVersion).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
                    baseline = publication is null ? null : AgentDefinitionAdminMapping.MapPublication(publication).Payload;
                }
                else baseline = builtIns is null ? null : await builtIns.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
                var sourceRow = await OwnedSandboxProfiles(db, tenant).Where(p => p.Id == c.ProfileId).OrderByDescending(p => p.Revision).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                if (baseline is null) throw new AccessDeniedException("definition_unavailable");
                var source = SandboxValue(sourceRow);
                if (source.RegistryCredentialId is { } credential)
                {
                    var value = await OwnedCredentials(db, tenant).SingleOrDefaultAsync(v => v.CredentialId == credential.ToString("D"), ct).ConfigureAwait(false);
                    TenantSandboxProfileRules.RequireCredential(selection.Target, value is null ? null : SqliteCredentialStore.Map(value), (clock ?? TimeProvider.System).GetUtcNow());
                }
                var pending = await db.Set<TenantSandboxVerificationRecord>().AnyAsync(a => a.TenantId == tenant && a.ProfileId == source.Id && a.InstanceId == null && a.Active, ct).ConfigureAwait(false);
                var verification = await db.Set<SandboxVerificationRow>().AsNoTracking().SingleOrDefaultAsync(v => v.TenantId == tenant && v.ProfileId == source.Id && v.ProfileRevision == source.Revision, ct).ConfigureAwait(false);
                var evidence = new SandboxVerificationEvidence(verification is null ? null : JsonSerializer.Deserialize<SandboxProfileVerification>(verification.Json, SandboxJson), pending);
                TenantSandboxControlRules.RequireSelectionSource(selection, agent, baseline, source, evidence);
                requested = TenantSandboxControlRules.ApplySelection(lease, selection, audit); db.Entry(row).CurrentValues.SetValues(SandboxRuntimePersistence.Row(requested)); return true;
            }, ct).ConfigureAwait(false);
        }
        catch (AccessDeniedException error) when (error.ReasonCode is "principal_unavailable" or "account_unavailable") { throw new AccessDeniedException("initiator_unavailable"); }
        return written ? requested : null;
    }
}
