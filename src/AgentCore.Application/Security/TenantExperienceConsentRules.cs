using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public static class TenantExperienceConsentRules
{
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(TenantExperienceOperationObservation value, bool manage = true) => TenantExperienceRules.Targets(value.Reader, value.Source?.Witness,
        value.Arguments.Tool == TenantExperienceToolContract.Consolidate, manage && !value.Arguments.ReadOnly);
    public static async ValueTask ReviewerAsync(TenantApprovalRequest request, TenantExperienceOperationObservation observed, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        await TenantExperienceRules.RawAsync(observed.Reader, observed.Source?.Witness, observed.Arguments.Tool == TenantExperienceToolContract.Consolidate, iam, ct, human,
            !observed.Arguments.ReadOnly, privateValue: !observed.Arguments.ReadOnly || observed.Source?.Witness.PrivateUserPrincipalId is not null).ConfigureAwait(false);
        await ReviewTargets(request, observed.Reader, Targets(observed), human, iam, ct).ConfigureAwait(false);
    }
    public static ValueTask ExecutorAsync(TenantApprovalRequest request, TenantExperienceOperationObservation value, IamAuthorizer iam, CancellationToken ct) =>
        TenantExperienceRules.RawAsync(value.Reader, value.Source?.Witness, value.Arguments.Tool == TenantExperienceToolContract.Consolidate, iam, ct,
            manage: !value.Arguments.ReadOnly, exceptional: request.Kind == ApprovalKind.ExceptionalGrant);
    public static string Completed(PreparedExperienceOperation value) => value.ToolName switch {
        TenantExperienceToolContract.Source => TenantExperienceRules.ReadCompleted,
        TenantExperienceToolContract.Record => TenantExperienceRules.Recorded,
        TenantExperienceToolContract.Consolidate => TenantExperienceRules.Consolidated,
        _ => throw new ArgumentException("Use an Experience operation.") };
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.ExperienceOperation is not { } value || value.ToolName == TenantExperienceToolContract.Source && request.Kind != ApprovalKind.Review
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied) || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != Completed(value)
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new SecurityConflictException("operation_mismatch");
    }
    private static async ValueTask ReviewTargets(TenantApprovalRequest request, TenantMemoryReader reader, IEnumerable<(string Permission, AccessScope Scope)> targets, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in targets.Distinct())
        {
            var primary = key == request.Permission && scope == TenantApprovalRules.Scope(request.TenantId, request.Operation);
            if (!primary && !await TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(reader.Security, PermissionCatalog.Get(key)).IndependentReview && request.OriginalInitiatorPrincipalId == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed
                || primary && request.Kind == ApprovalKind.ExceptionalGrant && !(await iam.CheckOwnedTenantAsync(reader.Security, human, PermissionCatalog.Get("approvals.grant_exception"), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("approver_unqualified");
        }
    }
    private static void SameTask(TenantApprovalRequest request, TenantMemoryReader reader)
    {
        if (request.TenantId != reader.Security.Tenant.TenantId || request.ExecutorPrincipalId != reader.Agent.PrincipalId || request.Operation.InstanceId != reader.Run.AgentInstanceId
            || request.SessionId != reader.Run.SessionId || request.RootActivationId != reader.Run.Admission.Activation.RootActivationId || request.Status != TenantApprovalStatus.Approved)
            throw new AccessDeniedException("experience_read_review_required");
    }
    public static async ValueTask OwnConsolidationAsync(TenantApprovalRequest request, TenantMemoryReader reader, TenantIsolationMetadata settings,
        TenantExperienceFactSource prior, TenantExperienceReference current, TenantExperience result, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        SameTask(request, reader);
        if (request.Operation.ExperienceOperation is not { ToolName: TenantExperienceToolContract.Consolidate } value
            || value.RootSessionId != reader.Root.SessionId || value.UserPrincipalId != reader.User?.PrincipalId || value.ExperienceRevision != settings.ExperienceRevision
            || reader.Run.Admission.Configuration?.ContinuitySettings?.AllowMaintenance != true || !TenantExperienceRules.Owns(reader, current)
            || current.Visibility != ExperienceVisibility.Superseded || current.Revision != prior.Revision + 1 || current.ContentHash != prior.ContentHash
            || !TenantExperienceRules.Sources(value).Any(i => i.ExperienceId == prior.ExperienceId && i.TenantId == prior.TenantId && i.ContentHash == prior.ContentHash && i.Revision == prior.Revision && i.Visibility == ExperienceVisibility.Eligible)
            || value.ResultExperienceId != result.ExperienceId || !TenantExperienceRules.Owns(reader, result.Reference()) || result.RootRunId != reader.Root.AgentRunId
            || result.SourceKind != ExperienceSourceKind.Consolidation || !result.Parents.Contains(prior.ExperienceId)) throw new AccessDeniedException("experience_source_changed");
        var args = TenantExperienceToolContract.Parse(value.ToolName, value.ArgumentsJson);
        if (result.ContentHash != TenantExperienceRules.ContentHash(args.Content!)) throw new AccessDeniedException("experience_source_changed");
        var observed = new TenantExperienceOperationObservation(reader, settings, args, null, [], value);
        await ExecutorAsync(request, observed, iam, ct).ConfigureAwait(false);
        await ReviewerAsync(request, observed, human, iam, ct).ConfigureAwait(false);
    }
    public static async ValueTask SourceAsync(TenantApprovalRequest request, TenantExperienceSourceObservation observed, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        SameTask(request, observed.Reader);
        if (request.Kind != ApprovalKind.Review || request.Operation.ExperienceOperation is not { ToolName: TenantExperienceToolContract.Source } value
            || value.Source != observed.Witness || value.RootSessionId != observed.Reader.Root.SessionId || value.UserPrincipalId != observed.Reader.User?.PrincipalId
            || value.ExperienceRevision != observed.Settings.ExperienceRevision) throw new AccessDeniedException("experience_read_review_required");
        var operation = new TenantExperienceOperationObservation(observed.Reader, observed.Settings, TenantExperienceToolContract.Parse(value.ToolName, value.ArgumentsJson), observed, [], value);
        await TenantExperienceRules.RawAsync(observed.Reader, observed.Witness, false, iam, ct).ConfigureAwait(false);
        await ReviewerAsync(request, operation, human, iam, ct).ConfigureAwait(false);
    }
    public static async ValueTask FactsAsync(TenantApprovalRequest request, TenantMemoryReader reader, TenantIsolationMetadata settings,
        IReadOnlyList<TenantExperienceReference> facts, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        SameTask(request, reader);
        if (request.Kind != ApprovalKind.Review || request.Operation.ContinuityRead?.Experience is not { } value || value.UserPrincipalId != reader.User?.PrincipalId
            || value.RootSessionId != reader.Root.SessionId || value.ExperienceRevision != settings.ExperienceRevision) throw new AccessDeniedException("experience_read_review_required");
        var expected = TenantExperienceFactRules.Sources(value);
        if (facts.Count != expected.Length || facts.Any(i => !TenantExperienceRules.Owns(reader, i) || !expected.Any(s => s.ExperienceId == i.ExperienceId && s.ContentHash == i.ContentHash && s.Revision == i.Revision))
            || facts.Any(i => i.UserPrincipalId is { } owner && owner != human)) throw new AccessDeniedException("experience_read_review_required");
        await TenantExperienceRules.RawAsync(reader, null, true, iam, ct).ConfigureAwait(false);
        await TenantExperienceRules.RawAsync(reader, null, true, iam, ct, human).ConfigureAwait(false);
        var read = request.Operation.ContinuityRead;
        var targets = TenantExperienceRules.Targets(reader, null, true, false).Concat(TenantContinuityRules.Targets(reader, TenantContinuityRules.Sessions(read).Select(s => s.SessionId)));
        await ReviewTargets(request, reader, targets, human, iam, ct).ConfigureAwait(false);
    }
}
