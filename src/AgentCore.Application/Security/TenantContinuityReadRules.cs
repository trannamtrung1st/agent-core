using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantContinuityReadRules
{
    public static async ValueTask<bool> OrdinaryAsync(TenantContinuityObservation observed, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(observed).Distinct())
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(observed.Reader.Security, observed.Reader.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) return false;
        return true;
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(TenantContinuityObservation observed) => TenantContinuityRules.Targets(observed.Reader,
        observed.Sessions.Select(s => s.SessionId), observed.Memory?.Items.Select(m => m.Reference()), observed.Memory is not null).Concat(observed.Experience is null ? [] : TenantExperienceRules.Targets(observed.Reader, null, true, false));
    public static async ValueTask RawAsync(TenantContinuityObservation observed, IamAuthorizer iam, CancellationToken ct, Guid? human = null)
    {
        await TenantContinuityRules.RequireRawAsync(observed.Reader,
        observed.Sessions.Select(s => s.SessionId), iam, ct, human, observed.Memory?.Items.Select(m => m.Reference()), observed.Memory is not null).ConfigureAwait(false);
        if (observed.Experience is { } experience)
        {
            if (human is { } viewer && experience.Items.Any(i => i.UserPrincipalId is { } owner && owner != viewer)) throw new AccessDeniedException("approval_unavailable");
            await TenantExperienceRules.RawAsync(observed.Reader, null, true, iam, ct, human).ConfigureAwait(false);
        }
    }
    public static async ValueTask ReviewerAsync(TenantApprovalRequest request, TenantContinuityObservation observed, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        await RawAsync(observed, iam, ct, human).ConfigureAwait(false);
        await RequireReviewerTargets(request, observed.Reader, Targets(observed), human, iam, ct).ConfigureAwait(false);
    }
    private static async ValueTask RequireReviewerTargets(TenantApprovalRequest request, TenantMemoryReader reader,
        IEnumerable<(string Permission, AccessScope Scope)> targets, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in targets.Distinct())
        {
            var primary = key == request.Permission && scope == TenantApprovalRules.Scope(request.TenantId, request.Operation);
            if (!primary && !await TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(reader.Security, PermissionCatalog.Get(key)).IndependentReview && request.OriginalInitiatorPrincipalId == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static async ValueTask ConsentAsync(TenantApprovalRequest request, TenantMemoryReader reader, IReadOnlyList<TenantMemoryReference> memory,
        Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (request.Kind != ApprovalKind.Review || request.Status != TenantApprovalStatus.Approved || request.Operation.ContinuityRead is not { } read
            || request.ExecutorPrincipalId != reader.Agent.PrincipalId || request.TenantId != reader.Security.Tenant.TenantId || request.SessionId != reader.Run.SessionId
            || read.SessionId != reader.Run.SessionId || read.RootSessionId != reader.Root.SessionId || read.UserPrincipalId != reader.User?.PrincipalId
            || request.RootActivationId != reader.Run.Admission.Activation.RootActivationId || request.Operation.InstanceId != reader.Run.AgentInstanceId
            || reader.Agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("continuity_read_review_required");
        var expectedMemory = read.Memory is null ? [] : TenantMemoryOperationRules.Sources(read.Memory);
        if (memory.Count != expectedMemory.Length || memory.Any(m => !TenantMemoryRules.Owns(reader, m) || !expectedMemory.Any(s => s.MemoryId == m.MemoryId && s.ContentHash == m.ContentHash)))
            throw new AccessDeniedException("continuity_read_review_required");
        var sessions = TenantContinuityRules.Sessions(read).Select(s => s.SessionId).ToArray();
        await TenantContinuityRules.RequireRawAsync(reader, sessions, iam, ct, memory: memory, includeMemory: read.Memory is not null).ConfigureAwait(false);
        await TenantContinuityRules.RequireRawAsync(reader, sessions, iam, ct, human, memory, read.Memory is not null).ConfigureAwait(false);
        await RequireReviewerTargets(request, reader, TenantContinuityRules.Targets(reader, sessions, memory, read.Memory is not null), human, iam, ct).ConfigureAwait(false);
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.ContinuityRead is null || request.Kind != ApprovalKind.Review || grant is not null || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != TenantContinuityRules.Completed) throw new SecurityConflictException("operation_mismatch");
    }
    public static void RequireSourcesShape(AgentRun run, IReadOnlyList<TenantHistoricalSource> sources)
    {
        if (sources.Count > 100 || sources.Distinct().Count() != sources.Count || sources.Any(s => s.TenantId != run.Owner.TenantId || s.SessionId == Guid.Empty
            || s.SessionId == run.SessionId || s.Revision < 1 || s.ThroughEntrySequence < 0 || s.AfterEntrySequence < 0 || s.Limit is < 1 or > 20
            || !TenantMemoryOperationRules.ValidHash(s.ObservationHash))) throw new AccessDeniedException("continuity_source_unavailable");
    }
    public static void RequireSource(TenantHistoricalSource source, TenantHistoricalSession current)
    {
        if (source.SessionId != current.SessionId || source.Revision != current.Revision || source.ThroughEntrySequence != current.ThroughEntrySequence
            || source.AfterEntrySequence != current.AfterEntrySequence || source.Limit != current.Limit || source.ObservationHash != current.ObservationHash)
            throw new AccessDeniedException("continuity_source_changed");
    }
}
