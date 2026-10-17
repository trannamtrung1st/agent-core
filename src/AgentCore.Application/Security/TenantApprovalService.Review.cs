using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantApprovalFieldChange(string Field, object? Before, object? After, bool BeforeAvailable = true,
    bool AfterAvailable = true, bool RestoresDefault = false);
public sealed record TenantApprovalReview(string AgentName, string? RequesterName, string? ReviewerName,
    IReadOnlyList<TenantApprovalFieldChange> Changes, bool CanDecide, bool CanCancel, string? DecisionUnavailableReason,
    bool IndependentReview, DateTimeOffset ServerNow, string ExecutionState);

public sealed partial class TenantApprovalService
{
    // Presentation and action hints only. POST decisions and effects revalidate current authority and witnesses.
    private async ValueTask<TenantApprovalReview> ReviewAsync(TenantApprovalView view, TenantSecurityState state,
        TenantSessionInitiator human, CancellationToken ct)
    {
        var r = view.Request; var op = r.Operation; var at = time.GetUtcNow();
        var target = await agents.FindTenantAgentAsync(r.TenantId, op.InstanceId, ct).ConfigureAwait(false);
        var snapshot = r.Snapshot;
        var matches = target is not null && target.Instance.Revision == op.ExpectedInstanceRevision && target.Instance.PersonaRevision == op.ExpectedPersonaRevision;
        if (snapshot is null && matches)
        {
            if (op.Kind == TenantLocalOperationKind.Persona) snapshot = TenantApprovalSnapshotRules.Persona(target!);
            else
            {
                try
                {
                    var (live, baseline) = await configuration.BaselineAsync(r.TenantId, op.InstanceId, ct).ConfigureAwait(false);
                    if (live.Instance.Revision == op.ExpectedInstanceRevision) snapshot = TenantApprovalSnapshotRules.Preferences(live, baseline);
                }
                catch (AccessDeniedException) { } // Historical requests remain readable when their definition is unavailable.
            }
        }
        async ValueTask<string?> Name(Guid? principal)
        {
            var user = state.Users.SingleOrDefault(u => u.PrincipalId == principal);
            return user is null ? null : (await security.ReadAccountAsync(user.AccountId, ct).ConfigureAwait(false))?.DisplayName;
        }
        var scope = AccessScope.ForResource(r.TenantId, TenantApprovalRequest.ResourceType, r.ResourceId);
        var policy = TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(TenantApprovalRequest.Permission));
        var source = await operations.FindRunAsync(new(r.TenantId, r.RunId), ct).ConfigureAwait(false);
        var waiting = source?.Status == AgentRunStatus.WaitingForApproval && source.ApprovalRequestId == r.RequestId;
        var cancel = r.Status == TenantApprovalStatus.Pending && at < r.ExpiresAt && waiting && target is not null
            && (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("approvals.cancel"),
                AccessScope.ForResource(r.TenantId, "approval", r.RequestId.ToString("D")), ct).ConfigureAwait(false)).Allowed;
        string? unavailable = r.Status != TenantApprovalStatus.Pending ? "request_terminal" : at >= r.ExpiresAt ? "approval_expired"
            : target is null || target.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active ? "executor_unavailable"
            : !matches ? "operation_witness_changed" : !waiting ? "run_unavailable" : null;
        if (unavailable is null)
        {
            try
            {
                var (run, current) = await operations.ResolveAsync(new(r.TenantId, r.RunId), ct).ConfigureAwait(false);
                if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
                TenantApprovalRules.Validate(r, run, target!);
                var effective = await configuration.ResolveAsync(r.TenantId, op.InstanceId, ct).ConfigureAwait(false);
                unavailable = TenantLocalEffectRules.Eligibility(run, effective.Agent, effective.Configuration.Configuration.Definition);
                if (unavailable is null)
                {
                    var eligible = await operations.Evaluate(state, run.Admission.ExecutionContext!, TenantApprovalRequest.Permission, scope, ct).ConfigureAwait(false);
                    if (eligible.Decision == OperationDecisionKind.Denied || r.Kind == ApprovalKind.Review && eligible.ApprovalKind == ApprovalKind.ExceptionalGrant)
                        unavailable = "approval_authority_changed";
                    else if (!await operations.QualifiesAsync(state, r.OriginalInitiatorPrincipalId, human.PrincipalId,
                        PermissionCatalog.Get(TenantApprovalRequest.Permission), scope, r.Kind, ct).ConfigureAwait(false))
                        unavailable = policy.IndependentReview && human.PrincipalId == r.OriginalInitiatorPrincipalId ? "independent_reviewer_required" : "approver_unqualified";
                }
            }
            catch (AccessDeniedException ex) { unavailable = ex.ReasonCode; }
            catch (SecurityConflictException ex) when (ex.ReasonCode != "stale_revision") { unavailable = ex.ReasonCode; }
            catch (AgentCoreException) { unavailable = "operation_configuration_changed"; }
        }
        var execution = view.OperationResult is not null ? "recorded" : r.Status == TenantApprovalStatus.Pending ? "waitingDecision"
            : r.Status != TenantApprovalStatus.Approved ? "notAuthorized" : "unrecorded";
        return new(snapshot?.AgentName ?? target?.Instance.Persona.Name ?? "Unavailable agent", await Name(r.OriginalInitiatorPrincipalId).ConfigureAwait(false),
            await Name(view.Decision?.HumanPrincipalId).ConfigureAwait(false), Changes(op, snapshot), unavailable is null, cancel, unavailable,
            policy.IndependentReview, at, execution);
    }

    private static IReadOnlyList<TenantApprovalFieldChange> Changes(PreparedTenantOperation op, TenantApprovalSnapshot? snapshot)
    {
        if (op.Persona is { } persona)
            return [new("name", snapshot?.BeforePersona?.Name, persona.Name, snapshot?.BeforePersona is not null),
                new("role", snapshot?.BeforePersona?.Role, persona.Role, snapshot?.BeforePersona is not null),
                new("description", snapshot?.BeforePersona?.Description, persona.Description, snapshot?.BeforePersona is not null),
                new("tone", snapshot?.BeforePersona?.Tone, persona.Tone, snapshot?.BeforePersona is not null)];
        var p = op.Preferences!; var defaults = snapshot?.PreferenceDefaults; var before = snapshot?.PreferenceOverrides;
        var values = new List<TenantApprovalFieldChange>();
        void Add(string field, object? original, object? value, bool restore, object? inherited) =>
            values.Add(new(field, original, restore ? inherited : value, snapshot is not null, !restore || defaults is not null, restore));
        if (p.ResponseLength is not null || p.ClearResponseLength) Add("responseLength", before?.ResponseLength ?? defaults?.ResponseLength, p.ResponseLength, p.ClearResponseLength, defaults?.ResponseLength);
        if (p.Language is not null || p.ClearLanguage) Add("language", before?.Language ?? defaults?.Language, p.Language, p.ClearLanguage, defaults?.Language);
        if (p.AskOneQuestionAtATime is not null || p.ClearAskOneQuestionAtATime) Add("askOneQuestionAtATime", before?.AskOneQuestionAtATime ?? defaults?.AskOneQuestionAtATime,
            p.AskOneQuestionAtATime, p.ClearAskOneQuestionAtATime, defaults?.AskOneQuestionAtATime);
        return values;
    }
}
