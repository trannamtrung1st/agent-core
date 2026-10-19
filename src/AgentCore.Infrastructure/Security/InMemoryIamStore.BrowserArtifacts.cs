using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private (int Captures, int Downloads) BrowserArtifactUsage(TenantApprovalRequest request) => TenantBrowserArtifactRules.Usage(
        _operationReceipts.Values.Where(r => r.TenantId == request.TenantId && r.RootActivationId == request.RootActivationId
            && r.ExecutorPrincipalId == request.ExecutorPrincipalId && r.SessionId == request.SessionId));
    private void RequireBrowserArtifactBudget(TenantApprovalRequest request)
    {
        if (request.Operation.BrowserCommand?.Artifacts is not { } policy) return;
        var usage = BrowserArtifactUsage(request);
        foreach (var run in sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == request.TenantId
            && r.Admission.Activation.RootActivationId == request.RootActivationId && r.Owner.ExecutorPrincipalId == request.ExecutorPrincipalId
            && r.SessionId == request.SessionId && r.SideEffect.Disposition == AgentRunSideEffectDisposition.InFlight))
            if (run.ApprovalRequestId is { } id && _approvals.GetValueOrDefault(id) is { } pending && !_operationReceipts.ContainsKey(pending.Operation.OperationId)
                && pending.Operation.BrowserCommand?.Artifacts is { } reserved)
            { if (reserved.Capture) usage.Captures++; if (reserved.Downloads) usage.Downloads++; }
        TenantBrowserArtifactRules.RequireBudget(policy, usage);
    }
    private string PublishBrowserArtifacts(TenantApprovalRequest request, AgentRun run, TenantBrowserArtifactBatch batch, CancellationToken ct)
    {
        if (artifactBacking is null) return TenantBrowserArtifactRules.Unavailable();
        try
        {
            var usage = BrowserArtifactUsage(request);
            batch = TenantBrowserArtifactRules.Bound(request.Operation.BrowserCommand!, request.Operation.OperationId, batch, usage.Captures, usage.Downloads);
            var expires = request.ExpiresAt;
            if (_operationGrants.GetValueOrDefault(request.RequestId)?.ExpiresAt is { } grantExpiry && grantExpiry < expires) expires = grantExpiry;
            artifactBacking.PublishBrowserBatchLocal(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), batch.Files, ApprovalNow, ct, expires);
            return TenantBrowserArtifactRules.Result(batch.Files, batch.UnavailableReason);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException or AgentCore.Application.Sessions.AgentCoreException)
        { return TenantBrowserArtifactRules.Unavailable(e is AgentCore.Application.Sessions.AgentCoreException ? "artifact_quota_exceeded" : "artifact_publication_unavailable"); }
    }
}
