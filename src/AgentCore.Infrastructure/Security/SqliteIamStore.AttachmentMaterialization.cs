using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<ArtifactRecord?> TryPublishMaterializationAsync(TenantAttachmentMutation proof, AttachmentRecord source, ArtifactRecord artifact, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var frozen = bytes.ToArray(); TenantAttachmentMaterializationRules.Publication(proof, source, artifact, frozen);
        var attachments = attachmentBacking ?? throw new AccessDeniedException("tenant_effect_unavailable");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (state, session, agent) = await RequireAttachmentHuman(db, proof, ct).ConfigureAwait(false);
        var item = await SqliteAttachmentStore.Owned(db, proof.Owner.TenantId, proof.Owner.SessionId, (clock ?? TimeProvider.System).GetUtcNow()).AsNoTracking().SingleOrDefaultAsync(a => a.AttachmentId == source.AttachmentId.ToString("D"), ct).ConfigureAwait(false);
        if (item is null || SqliteAttachmentStore.Domain(item) != source) throw new AccessDeniedException("resource_unavailable");
        await TenantAttachmentMaterializationRules.RequireAsync(state, session, agent, proof.Human.PrincipalId, source, new IamAuthorizer(this), (clock ?? TimeProvider.System).GetUtcNow(), ct).ConfigureAwait(false);
        if (state.Tenant.Revision != proof.SecurityRevision || agent.Instance.Revision != proof.InstanceRevision) return null;
        TenantAttachmentRules.Content(source, await attachments.ReadTenantContentAsync(proof.Owner.TenantId, proof.Owner.SessionId, source.AttachmentId, ct).ConfigureAwait(false));
        var staged = await (artifactBacking ?? throw new AccessDeniedException("tenant_effect_unavailable")).StageHumanLocalAsync(db, proof, source, artifact, frozen, ct).ConfigureAwait(false);
        // Current login and time fences repeat after filesystem I/O. Tenant CAS detects concurrent IAM/lifecycle writers.
        await RequireHumanSessionAsync(db, proof.Owner.TenantId, proof.Human, ct).ConfigureAwait(false);
        if (!TenantAttachmentRules.Available(source, (clock ?? TimeProvider.System).GetUtcNow())) throw new AccessDeniedException("resource_unavailable");
        db.Artifacts.Add(staged); db.Add(proof.Audit);
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == proof.Owner.TenantId, ct).ConfigureAwait(false);
        db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) });
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return artifact; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException error) when (IsConstraint(error)) { return null; }
    }
}
