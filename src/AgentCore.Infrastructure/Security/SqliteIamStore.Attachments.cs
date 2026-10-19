using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantAttachmentMutationStore
{
    private async ValueTask<(TenantSecurityState State, TenantSession Session, TenantAgent Agent)> RequireAttachmentHuman(AgentCoreDbContext db, TenantAttachmentMutation proof, CancellationToken ct)
    {
        await RequireHumanSessionAsync(db, proof.Owner.TenantId, proof.Human, ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, proof.Owner.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var session = await ReadSandboxSession(db, proof.Owner.TenantId, proof.Owner.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var row = await OwnedAgents(db, proof.Owner.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == proof.Owner.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = Owned(row); TenantAttachmentRules.Session(proof.Owner, session, agent, true); return (state, session, agent);
    }
    public async ValueTask<AttachmentRecord?> TryUploadAttachmentAsync(TenantAttachmentMutation proof, TenantAttachmentUpload upload, CancellationToken ct = default)
    {
        upload = upload with { Bytes = upload.Bytes.ToArray() }; TenantAttachmentRules.Mutation(proof, "attachments.create");
        var files = attachmentBacking ?? throw new AccessDeniedException("tenant_effect_unavailable");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (state, session, agent) = await RequireAttachmentHuman(db, proof, ct).ConfigureAwait(false);
        await TenantAttachmentRules.PermissionsAsync(state, proof.Human.PrincipalId, proof.Owner.SessionId, null, "attachments.create", new IamAuthorizer(this), ct).ConfigureAwait(false);
        TenantAttachmentRules.Upload(proof, upload, (clock ?? TimeProvider.System).GetUtcNow());
        var prior = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == proof.Owner.TenantId && a.SessionId == proof.Owner.SessionId.ToString("D") && a.UploadRequestId == proof.RequestId, ct).ConfigureAwait(false);
        if (prior is not null) return TenantAttachmentRules.SameUpload(SqliteAttachmentStore.Domain(prior), upload.Record) && TenantAttachmentRules.Available(SqliteAttachmentStore.Domain(prior), (clock ?? TimeProvider.System).GetUtcNow())
            ? SqliteAttachmentStore.Domain(prior) : throw new SecurityConflictException("attachment_upload_mismatch");
        if (await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == proof.RequestId, ct).ConfigureAwait(false)) throw new SecurityConflictException("identity_conflict");
        if (state.Tenant.Revision != proof.SecurityRevision || session.Snapshot.Revision != proof.SessionRevision || agent.Instance.Revision != proof.InstanceRevision) return null;
        var staged = await files.StageTenant(db, proof.Owner, upload, ct).ConfigureAwait(false); db.Attachments.Add(staged);
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == state.Tenant.TenantId, ct).ConfigureAwait(false); db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) }); db.Add(proof.Audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return upload.Record; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException error) when (IsConstraint(error)) { return null; }
    }
    public async ValueTask<bool> TryAbortAttachmentAsync(TenantAttachmentMutation proof, Guid attachmentId, CancellationToken ct = default)
    {
        TenantAttachmentRules.Mutation(proof, "attachments.delete", attachmentId);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (state, session, agent) = await RequireAttachmentHuman(db, proof, ct).ConfigureAwait(false);
        var prior = await db.Set<AuthorizationAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.AuditId == proof.RequestId, ct).ConfigureAwait(false);
        if (prior is not null) return prior.TenantId == proof.Owner.TenantId && prior.ActorPrincipalId == proof.Human.PrincipalId && prior.Action == proof.Audit.Action && prior.Target == proof.Audit.Target
            ? true : throw new SecurityConflictException("identity_conflict");
        var row = await SqliteAttachmentStore.Owned(db, proof.Owner.TenantId, proof.Owner.SessionId).SingleOrDefaultAsync(a => a.AttachmentId == attachmentId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (!TenantAttachmentRules.Available(SqliteAttachmentStore.Domain(row), (clock ?? TimeProvider.System).GetUtcNow())) throw new AccessDeniedException("resource_unavailable");
        await TenantAttachmentRules.PermissionsAsync(state, proof.Human.PrincipalId, proof.Owner.SessionId, attachmentId, "attachments.delete", new IamAuthorizer(this), ct).ConfigureAwait(false);
        if (row.State != nameof(AttachmentState.Pending)) throw new SecurityConflictException("attachment_already_bound");
        if (state.Tenant.Revision != proof.SecurityRevision || session.Snapshot.Revision != proof.SessionRevision || agent.Instance.Revision != proof.InstanceRevision) return false;
        row.ExpiresAtUtc = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds(); row.StageForNextTurn = false;
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) }, Assignments = state.Assignments.Where(a => a.Scope != TenantAttachmentRules.Scope(state.Tenant.TenantId, attachmentId)).ToArray() };
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == state.Tenant.TenantId, ct).ConfigureAwait(false); db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(proof.Audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException error) when (IsConstraint(error)) { return false; }
    }
}
