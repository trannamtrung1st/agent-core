using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantApprovedOperationStore
{
    public async ValueTask<TenantOperationReceipt?> FindDirectOperationReceiptAsync(Guid tenantId, Guid runId, string sourceCallId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RunId == runId.ToString("D") && r.SourceCallId == sourceCallId, ct).ConfigureAwait(false))?.Domain();
    }
    public async ValueTask<TenantApprovalDecision?> FindApprovalDecisionAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenantId && d.RequestId == requestId, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantOperationReceipt?> FindOperationReceiptAsync(Guid tenantId, Guid operationId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.OperationId == operationId, ct).ConfigureAwait(false))?.Domain();
    }

    public async ValueTask<TenantOperationReceipt?> TryCommitLocalEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == commit.OperationId, ct).ConfigureAwait(false);
        if (existing is not null) return TenantLocalEffectRules.MatchingReceipt(existing.Domain(), next.Tenant.TenantId, commit);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (requestRow is null || decision is null || row is null || row.Revision != commit.ExpectedRunRevision) return null;
        var request = requestRow.Domain(); var run = AgentRunStoreMapping.ToDomain(row);
        var sourceRow = request.RunId != run.AgentRunId ? await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false) : null;
        var source = sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow);
        var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.TenantId == tenant.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantLocalEffectRules.Validate(request, decision, grant, run, commit, source);
        var owned = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (owned is null) return null;
        var target = Owned(owned); TenantSessionRules.RequireExecutor(before, target);
        await ValidateTenantActorAsync(db, before, request.ExecutorPrincipalId, ct).ConfigureAwait(false);
        var at = (clock ?? TimeProvider.System).GetUtcNow();
        commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at, source);
        var correlated = await RootApproval(db, request.TenantId, request.RootActivationId.ToString("D"), request.ExecutorPrincipalId, request.Operation).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (correlated is not null && correlated.RequestId != request.RequestId)
            commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "root_operation_conflict" };
        if (commit.Outcome == TenantOperationOutcome.Completed)
        {
            try { await ValidateTenantActorAsync(db, before, decision.HumanPrincipalId, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "grantor_authority_lost" }; }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.OriginalInitiatorPrincipalId is { } requester)
                try { await ValidateTenantActorAsync(db, before, requester, ct).ConfigureAwait(false); }
                catch (AccessDeniedException) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "requester_unavailable" }; }
        }
        audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
        TenantLocalEffectRules.ValidateTransaction(next, expectedSecurityRevision, request, audit);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        at = (clock ?? TimeProvider.System).GetUtcNow();
        commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at, source);
        audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
        (SandboxLease Lease, SandboxExecution? Execution)? control = null;
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.SandboxControl is not null)
        {
            control = await RequireSandboxControlOperation(db, request, run, target, ct).ConfigureAwait(false);
            var session = (await ReadSandboxSession(db, request.TenantId, request.SessionId, ct).ConfigureAwait(false))!;
            await TenantSandboxControlOperationRules.RequirePermissionsAsync(before, session.Owner, request.Operation.SandboxControl.ProfileId,
                decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.SandboxRead is not null)
        {
            var owner = await RequireSandboxReadOperation(db, request, run, target, ct).ConfigureAwait(false);
            await TenantSandboxReadOperationRules.RequireReviewerAsync(before, owner, request.Operation.SandboxRead, request.OriginalInitiatorPrincipalId,
                decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.WorkspaceRead is not null)
        {
            await RequireWorkspaceReadOperation(db, request, run, target, ct).ConfigureAwait(false);
            await TenantWorkspaceReadOperationRules.ReviewerAsync(before, target.Instance.InstanceId, request.Operation.WorkspaceRead, request.OriginalInitiatorPrincipalId,
                decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.AttachmentRead is not null)
        {
            await RequireAttachmentReadOperation(db, request, run, target, ct).ConfigureAwait(false);
            await TenantAttachmentReadRules.ReviewerAsync(before, request.Operation.AttachmentRead, request.OriginalInitiatorPrincipalId,
                decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.CompletionRead is not null)
        {
            await RequireCompletionReadOperation(db, request, run, target, ct).ConfigureAwait(false);
            await TenantCompletionReadRules.ReviewerAsync(before, request.Operation.CompletionRead, request.OriginalInitiatorPrincipalId,
                decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.ArtifactVerification is not null)
        {
            await RequireArtifactVerificationOperation(db, request, run, target, ct).ConfigureAwait(false);
            await TenantArtifactVerificationRules.ReviewerAsync(before, request.Operation.ArtifactVerification, request.OriginalInitiatorPrincipalId,
                decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.AccountRead is not null)
        {
            await RequireAccountReadOperation(db, request, run, target, ct).ConfigureAwait(false);
            await TenantAccountReadRules.ReviewerAsync(before, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.CredentialRead is not null)
        {
            await RequireCredentialReadOperation(db, request, run, target, ct).ConfigureAwait(false);
            await TenantCredentialReadOperationRules.ReviewerAsync(before, request.Operation.CredentialRead, request.OriginalInitiatorPrincipalId,
                decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
        ArtifactRecordRow? artifactRow = null;
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.ArtifactPublication is not null)
        {
            try
            {
                _ = await RequireArtifactOperation(db, request, run, target, ct, source).ConfigureAwait(false);
                try { await TenantArtifactReviewRules.ReviewerAsync(before, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct, ownedSources: true).ConfigureAwait(false); }
                catch (AccessDeniedException) { throw new AccessDeniedException("grantor_authority_lost"); }
                var artifact = await TenantArtifactSourceRules.MaterializeAsync(run, request.Operation.ArtifactPublication, commit.ArtifactSourceContent, ct).ConfigureAwait(false);
                var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, commit.Generation); var bytes = artifact.Bytes;
                artifactRow = await (artifactBacking ?? throw new AccessDeniedException("tenant_effect_unavailable")).StageTenantLocalAsync(db, frame,
                    TenantArtifactRules.Record(frame, commit.OperationId, artifact.DisplayName, artifact.ContentType, bytes, at), bytes, ct).ConfigureAwait(false);
                if (commit.ArtifactSourceContent is { } currentSource) await currentSource.RequireCurrentAsync(ct).ConfigureAwait(false);
            }
            catch (AccessDeniedException e) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = e.ReasonCode }; }
            catch (SecurityConflictException e) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = e.ReasonCode }; }
            at = (clock ?? TimeProvider.System).GetUtcNow(); commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at, source);
            audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
        }
        TenantSessionRoot? childRoot = null;
        if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.BackgroundStart is not null)
        {
            var parent = await RequireBackgroundCreationOperation(db, request, run, target, ct).ConfigureAwait(false);
            await TenantBackgroundCreationRules.ReviewerAsync(before, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            childRoot = TenantBackgroundCreationRules.Admission(request, run, parent, target, commit.BackgroundAdmission!, at);
        }
        audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, grant?.GrantId);
        var complete = commit.Outcome == TenantOperationOutcome.Completed;
        var configure = complete && request.Operation.IsLocalConfiguration;
        var updated = configure ? TenantAgentConfigurationRules.Apply(target.Instance,
            TenantLocalEffectRules.Change(request, target.Instance), at) : target.Instance;
        var savedRun = run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, complete, at);
        if (configure)
        {
            db.Attach(owned.Instance);
            if (request.Operation.Kind == TenantOperationKind.Persona)
            {
                owned.Instance.PersonaJson = JsonSerializer.Serialize(updated.Persona, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                owned.Instance.PersonaRevision = updated.PersonaRevision;
            }
            else owned.Instance.SettingsOverridesJson = JsonSerializer.Serialize(updated.SettingsOverrides, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            owned.Instance.Revision = updated.Revision; owned.Instance.UpdatedAtUtc = at.ToUnixTimeMilliseconds();
        }
        if (complete && control is { } current)
        {
            var pending = request.Operation.SandboxControl!.Kind == SandboxControlKind.ResetEnvironment ? TenantSandboxControlRules.Apply(current.Lease)
                : current.Lease with { Revision = checked(current.Lease.Revision + 1), SafeMessage = "Stop requested. Process cleanup is pending; canonical files are retained." };
            var leaseRow = await db.Set<SandboxLeaseRow>().SingleAsync(l => l.TenantId == request.TenantId && l.SessionId == request.SessionId.ToString("D"), ct).ConfigureAwait(false);
            db.Entry(leaseRow).CurrentValues.SetValues(SandboxRuntimePersistence.Row(pending));
            if (request.Operation.SandboxControl.Kind == SandboxControlKind.StopProcess)
            {
                var stopped = TenantSandboxControlRules.ApplyStop(current.Execution!, audit with { AuditId = request.RequestId, ActorPrincipalId = decision.HumanPrincipalId });
                var executionRow = await db.Set<SandboxExecutionRow>().SingleAsync(e => e.TenantId == request.TenantId && e.SessionId == leaseRow.SessionId && e.Id == stopped.Id.ToString("D"), ct).ConfigureAwait(false);
                db.Entry(executionRow).CurrentValues.SetValues(SandboxRuntimePersistence.Row(stopped));
            }
        }
        if (complete && artifactRow is not null) db.Artifacts.Add(artifactRow);
        if (complete && childRoot is not null)
        {
            var child = commit.BackgroundAdmission!;
            await StageTenantBackgroundGraph(db, childRoot, child.Session, child.Run, ct).ConfigureAwait(false);
        }
        AgentRunStoreMapping.Apply(row, savedRun);
        if (grant is not null) db.Entry(grant).CurrentValues.SetValues(grant with { Status = complete ? OperationGrantStatus.Consumed : OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) });
        var receipt = new TenantOperationReceipt(commit.OperationId, request.RequestId, request.TenantId, run.AgentRunId, run.SessionId,
            run.Admission.Activation.RootActivationId, request.ExecutorPrincipalId, target.Instance.InstanceId, commit.ActionHash, commit.Outcome, commit.ReasonCode,
            updated.Revision, updated.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()));
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
    }
}
