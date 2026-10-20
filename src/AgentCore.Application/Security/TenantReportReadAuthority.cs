using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Report creation authority never substitutes for reading its private source.
public static class TenantReportReadRules
{
    public static async ValueTask RequireAsync(TenantSecurityState state, TenantBackgroundCompletion source, IamAuthorizer iam, CancellationToken ct)
    {
        await RequireRawAsync(state, source, iam, ct).ConfigureAwait(false);
        if (await NeedsReviewAsync(state, source, iam, ct).ConfigureAwait(false)) throw new AccessDeniedException("completion_read_review_required");
    }
    private static IEnumerable<(string Permission, AccessScope Scope)> Targets(TenantBackgroundCompletion source)
    {
        var initial = source.InitialChild ?? source.Child; var tenant = source.Child.Owner.TenantId!.Value;
        return new[] { ("sessions.read", TenantSessionRules.Scope(tenant, source.Child.SessionId)),
            ("agent-runs.read", TenantRunRules.Scope(tenant, initial.AgentRunId)), ("agent-runs.read", TenantRunRules.Scope(tenant, source.Child.AgentRunId)) }.Distinct();
    }
    public static async ValueTask RequireRawAsync(TenantSecurityState state, TenantBackgroundCompletion source, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(source))
        {
            var permission = PermissionCatalog.Get(key);
            if (!(await iam.CheckOwnedTenantAsync(state, source.Child.Owner.ExecutorPrincipalId!.Value, permission, scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("completion_source_read_required");
        }
    }
    public static async ValueTask<bool> NeedsReviewAsync(TenantSecurityState state, TenantBackgroundCompletion source, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(source))
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, source.Child.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) return true;
        return false;
    }
    public static async ValueTask<TenantBackgroundCompletion> SourceAsync(AgentRun report,
        Func<Guid, CancellationToken, ValueTask<AgentRun?>> runs, Func<Guid, CancellationToken, ValueTask<TenantBackgroundCompletion?>> sources, CancellationToken ct)
    {
        var current = report; var seen = new HashSet<Guid>();
        for (var depth = 0; depth < 64; depth++)
        {
            if (!seen.Add(current.AgentRunId) || current.Owner != report.Owner || current.SessionId != report.SessionId
                || current.Admission.OutputContract != AgentRunOutputContract.CompletionReport) break;
            var context = current.Admission.ExecutionContext;
            if (context?.Origin == ExecutionOrigin.CompletionReport && context.ParentRunId is { } child)
            {
                var source = await sources(child, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_unavailable");
                TenantBackgroundCompletionFactory.RequireParent(source);
                if (source.Child.Owner != report.Owner || source.Parent!.Owner.SessionId != report.SessionId || source.Child.AgentRunId != child
                    || current.Admission.Activation.SourceAgentRunId != child || current.Admission.Activation.SourceSessionId != source.Child.SessionId)
                    throw new AccessDeniedException("completion_unavailable");
                return source;
            }
            if (context?.Origin != ExecutionOrigin.ApprovalContinuation || current.Admission.ApprovalResume?.SourceRunId != context.ParentRunId
                || context.ParentRunId is not { } parent || await runs(parent, ct).ConfigureAwait(false) is not { } previous
                || previous.Result?.OutcomeKind != AgentRunOutcomeKind.Continued || previous.Result.ContinuationRunId != current.AgentRunId) break;
            current = previous;
        }
        throw new AccessDeniedException("completion_unavailable");
    }
}

public sealed class TenantReportReadAuthority(ITenantRuntimeStore storage, ITenantRunStore runs, IIamStore security, IamAuthorizer iam, TenantReportSourceReadService? sourceReads = null)
{
    public async ValueTask RequireAsync(AgentRun report, CancellationToken ct)
    {
        if (report.Admission.OutputContract != AgentRunOutputContract.CompletionReport) return;
        var tenant = report.Owner.TenantId!.Value;
        var source = await TenantReportReadRules.SourceAsync(report, (id, token) => runs.FindTenantRunAsync(tenant, id, token),
            (id, token) => storage.ReadTenantBackgroundCompletionAsync(report.Owner, id, token), ct).ConfigureAwait(false);
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (sourceReads is not null) await sourceReads.RequireAuthorizedAsync(source, state, ct).ConfigureAwait(false);
        else await TenantReportReadRules.RequireAsync(state, source, iam, ct).ConfigureAwait(false);
        if ((await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("completion_source_changed");
    }
}
