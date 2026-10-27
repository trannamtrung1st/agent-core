using AgentCore.Application.Ports;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    private async ValueTask<TenantApprovalRequest> ChangeEvaluationSandboxAsync(TenantSecurityState state, TenantApprovalRequest request,
        TenantSessionInitiator human, TenantApprovalStatus outcome, CancellationToken ct)
    {
        if (outcome != TenantApprovalStatus.Cancelled)
        {
            var authority = evaluationSandbox ?? throw new AccessDeniedException("approval_unavailable");
            var execution = await ((ITenantEvaluationStore)security).FindSandboxEvaluationAsync(request.TenantId, request.Operation.SandboxCommand!.Evaluation!.ExecutionId, ct)
                ?? throw new AccessDeniedException("approval_unavailable");
            await authority.RequireAsync(execution, ct);
            await authority.ReviewerAsync(state, request, human.PrincipalId, ct);
        }
        var at = time.GetUtcNow(); var next = request.Decide(outcome, request.Revision, at);
        var decision = new TenantApprovalDecision(ids.NewId(), request.RequestId, request.TenantId, human.PrincipalId, outcome, request.Operation.ActionHash, next.Revision, at);
        var signal = new ApprovalProcessedSignal(ids.NewId(), request.TenantId, request.RequestId, request.RunId, next.Revision, 1, at, null);
        var audit = new AuthorizationAudit(ids.NewId(), request.TenantId, human.PrincipalId, "approvals.decide", request.RequestId.ToString("D"), "allowed", "evaluation_sandbox_review_decided", at);
        if (!await approvals.TryDecideApprovalAsync(Next(state), state.Tenant.Revision, next, request.Revision, decision, null, signal, human, audit, ct))
            throw new SecurityConflictException("stale_revision");
        return next;
    }
    private async ValueTask<TenantApprovalReview> ReviewEvaluationSandboxAsync(TenantApprovalView view, TenantSecurityState state, TenantSessionInitiator human, CancellationToken ct)
    {
        var request = view.Request; var value = request.Operation.SandboxCommand!; var reference = value.Evaluation!;
        var canDecide = false; string? reason = null;
        try
        {
            var authority = evaluationSandbox ?? throw new AccessDeniedException("approval_unavailable");
            var execution = await ((ITenantEvaluationStore)security).FindSandboxEvaluationAsync(request.TenantId, reference.ExecutionId, ct) ?? throw new AccessDeniedException("approval_unavailable");
            await authority.RequireAsync(execution, ct); await authority.ReviewerAsync(state, request, human.PrincipalId, ct);
            canDecide = request.Status == TenantApprovalStatus.Pending && time.GetUtcNow() < request.ExpiresAt;
        }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
        { reason = error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode; }
        var cancel = await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("approvals.cancel"), AccessScope.ForResource(request.TenantId, "approval", request.RequestId.ToString("D")), ct);
        async ValueTask<string?> Name(Guid? principal)
        {
            var user = state.Users.SingleOrDefault(u => u.PrincipalId == principal);
            return user is null ? null : (await security.ReadAccountAsync(user.AccountId, ct))?.DisplayName;
        }
        return new(request.Snapshot?.AgentName ?? "Evaluation Sandbox", await Name(request.OriginalInitiatorPrincipalId), await Name(view.Decision?.HumanPrincipalId), [
            new("command", null, TenantApprovalRules.SandboxCommand(value).Command, false),
            new("sandboxProfile", null, value.ProfileId, false), new("timeoutSeconds", null, TenantApprovalRules.SandboxCommand(value).TimeoutSeconds, false),
            new("Evaluation attempt", null, reference.AttemptId.ToString("D"), false), new("Execution identity", null, reference.ExecutionId, false), new("Script version / hash", null, $"{reference.Script.ScriptId:D} · v{reference.Script.Version} · {reference.Script.ContentHash}", false),
            new("Input digest", null, reference.InputHash, false),
            new("Isolation", null, "Fresh disposable environment; authorized terminal Run evidence at read-only /home/input.json. Scratch /working. No original workspace or secrets. Egress denied. Profile setup scripts do not run.", false)
        ], canDecide, request.Status == TenantApprovalStatus.Pending && cancel.Allowed, reason,
            TenantOperationPolicyService.Effective(state, PermissionCatalog.Get("sandbox.execute")).IndependentReview, time.GetUtcNow(), "Independent evaluation admission; source Run stays unchanged");
    }
}
