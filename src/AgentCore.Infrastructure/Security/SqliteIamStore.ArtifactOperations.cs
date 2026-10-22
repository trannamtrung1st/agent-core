using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantArtifactOperationStore
{
    public async ValueTask<TenantOperationReceipt?> TryCommitArtifactAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantArtifactCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, next.Tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row);
        var existing = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == next.Tenant.TenantId && r.RunId == row.AgentRunId && r.SourceCallId == commit.SourceCallId, ct).ConfigureAwait(false);
        if (existing is not null) return TenantArtifactOperationRules.Match(existing.Domain(), run, commit.SourceCallId, commit.ParametersHash);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var owned = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == row.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        var target = Owned(owned); TenantSessionRules.RequireExecutor(before, target);
        await ValidateTenantActorAsync(db, before, target.PrincipalId, ct).ConfigureAwait(false);
        if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } requester) await ValidateTenantActorAsync(db, before, requester, ct).ConfigureAwait(false);
        await RequireTenantExecutionSourcesAsync(db, run, before, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        var at = (clock ?? TimeProvider.System).GetUtcNow(); var args = TenantArtifactOperationRules.Validate(run, target, commit, at);
        var publication = new PreparedArtifactPublication(run.SessionId, args.CanonicalArguments, commit.Source);
        var prepared = TenantApprovalRules.PrepareArtifact(commit.OperationId, run.AgentInstanceId, publication, target.Instance.Revision, target.Instance.PersonaRevision);
        var correlated = await RootApproval(db, before.Tenant.TenantId, run.Admission.Activation.RootActivationId.ToString("D"), target.PrincipalId, prepared).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (correlated is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(correlated.Domain()));
        var definition = await TenantAgentDefinitionBaseline(db, target, ct).ConfigureAwait(false);
        if (!AgentCore.Application.Tools.TenantTaskExecutionContract.Allows(run, definition) || !AgentCore.Application.Tools.TenantArtifactToolContract.AllowsPublication(definition, publication)) throw new AccessDeniedException("capability_unavailable");
        await TenantArtifactOperationRules.RequireAuthorityAsync(before, run, new IamAuthorizer(this), ct).ConfigureAwait(false);
        if (args.SourcePath is { } path)
        {
            var session = await ReadSandboxSession(db, before.Tenant.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            TenantArtifactSourceRules.Require(run, target, definition, session, path, commit.Source);
            if (commit.Source?.ResourceKey is { } sourceKey) await RequireResourcePin(db, sourceKey, commit.Source.ResourceRevision, run, target, ct).ConfigureAwait(false);
            if (commit.Source is { AttachmentId: { } attachmentId } attachmentSource)
            {
                var attachment = await SqliteAttachmentStore.Owned(db, run.Owner.TenantId!.Value, run.SessionId).SingleOrDefaultAsync(a => a.AttachmentId == attachmentId.ToString("D") && a.ContentDeletedAtUtc == null, ct).ConfigureAwait(false);
                TenantAttachmentArtifactRules.Require(attachmentSource, session.Owner, attachment is null ? null : SqliteAttachmentStore.Domain(attachment));
            }
            await TenantArtifactSourceRules.RequireAuthorityAsync(before, run, path, new IamAuthorizer(this), false, ct, ownedSources: true).ConfigureAwait(false);
        }
        TenantArtifactOperationRules.Transaction(next, expectedSecurityRevision, run, audit);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        var materialized = await TenantArtifactSourceRules.MaterializeAsync(run, publication, commit.SourceContent, ct).ConfigureAwait(false);
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, commit.Generation); var bytes = materialized.Bytes;
        var artifact = TenantArtifactRules.Record(frame, commit.OperationId, materialized.DisplayName, materialized.ContentType, bytes, at);
        var staged = await (artifactBacking ?? throw new AccessDeniedException("tenant_effect_unavailable")).StageTenantLocalAsync(db, frame, artifact, bytes, ct).ConfigureAwait(false);
        if (commit.SourceContent is { } currentSource) await currentSource.RequireCurrentAsync(ct).ConfigureAwait(false);
        at = (clock ?? TimeProvider.System).GetUtcNow(); _ = TenantArtifactOperationRules.Validate(run, target, commit, at);
        var savedRun = run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.OperationId, commit.ActionHash, at);
        audit = TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, commit.OperationId);
        var receipt = new TenantOperationReceipt(commit.OperationId, null, tenant.TenantId, run.AgentRunId, run.SessionId,
            run.Admission.ExecutionContext.RootActivationId, target.PrincipalId, run.AgentInstanceId, commit.ActionHash, TenantOperationOutcome.Completed,
            "operation_completed", target.Instance.Revision, target.Instance.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), commit.SourceCallId, commit.ParametersHash);
        db.Artifacts.Add(staged); AgentRunStoreMapping.Apply(row, savedRun); db.Add(TenantOperationReceiptRecord.From(receipt));
        db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit with { At = at });
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
    }
}
