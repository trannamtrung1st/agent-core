using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantWorkspaceEffectRules
{
    public const string Dispatch = "operations.workspace.mutation.dispatch";
    public const string Result = "operations.workspace.mutation";
    public const string Completed = "workspace_mutation_completed";
    public const string Uncertain = "workspace_mutation_uncertain";
    public static TenantWorkspaceReference Owner(AgentRun run, string path) => new(run.Owner.TenantId!.Value, run.AgentInstanceId,
        path.StartsWith("/working/", StringComparison.Ordinal) ? run.SessionId : null);
    public static void Session(AgentRun run, TenantAgent agent, TenantSession session)
    {
        if (!TenantWorkspaceMutationRules.Matches(new(agent.TenantId, agent.Instance.InstanceId, run.SessionId), agent, session)
            || !TenantWorkspaceMutationRules.Writable(agent, session) || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active)
            throw new AccessDeniedException("resource_unavailable");
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(AgentRun run, TenantWorkspaceMutationArguments args)
    {
        yield return ("sessions.read", TenantSessionRules.Scope(run.Owner.TenantId!.Value, run.SessionId));
        if (args.Targets.Any(t => t.Path.StartsWith("/home/", StringComparison.Ordinal)))
            yield return ("agents.read", AccessScope.ForResource(run.Owner.TenantId.Value, "agent", run.AgentInstanceId.ToString("D")));
        foreach (var t in args.Targets.Select(t => (t.Permission, TenantWorkspaceRules.Scope(Owner(run, t.Path)))).Distinct()) yield return t;
    }
    public static TenantWorkspaceMutationArguments Validate(AgentRun run, TenantAgent agent, AgentDefinition current, AgentRunDirectOperation operation, Guid generation, DateTimeOffset at, bool started = false)
    {
        if (run.Owner.TenantId is null || run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != generation || run.Claim.LeaseExpiresAtUtc <= at
            || run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation)) throw new AccessDeniedException("tenant_runtime_unavailable");
        if (agent.TenantId != run.Owner.TenantId || agent.Instance.InstanceId != run.AgentInstanceId || agent.PrincipalId != run.Owner.ExecutorPrincipalId
            || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || agent.Instance.Revision != operation.InstanceRevision || agent.Instance.PersonaRevision != operation.PersonaRevision)
            throw new SecurityConflictException("workspace_source_changed");
        var args = TenantWorkspaceMutationContract.Parse(run.SessionId, operation.ToolName, operation.ArgumentsJson);
        if (args.CanonicalArguments != operation.ArgumentsJson || args.ParametersHash != operation.ParametersHash || operation.OperationId == Guid.Empty)
            throw new SecurityConflictException("operation_mismatch");
        if (!TenantWorkspaceMutationContract.Allows(run.Admission.Configuration!.Definition, operation.ToolName) || !TenantWorkspaceMutationContract.Allows(current, operation.ToolName))
            throw new AccessDeniedException("capability_unavailable");
        var isolation = StateIsolationAccess.RestrictDefinition(run.Admission.Configuration.Definition, current).StateIsolationPolicy ?? StateIsolationPolicy.Default;
        foreach (var (path, permission) in args.Targets)
            StateIsolationAccess.RequireHome(isolation, path, permission != "workspaces.read");
        if (!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) throw new SecurityConflictException("workspace_checkpoint_required");
        var pending = AgentRunToolCallCheckpoint.PendingCalls(messages!).ToArray();
        if (pending.Length != 1 || pending[0].Id != operation.SourceCallId || pending[0].Name != operation.ToolName
            || TenantWorkspaceMutationContract.Parse(run.SessionId, pending[0].Name, pending[0].ArgumentsJson).ParametersHash != operation.ParametersHash)
            throw new SecurityConflictException("workspace_checkpoint_required");
        if (started && (run.SideEffect.ExternalEffect?.DirectOperation != operation || run.SideEffect.Disposition != AgentRunSideEffectDisposition.InFlight
            || run.SideEffect.ToolCallId != "operation:" + operation.OperationId.ToString("D") || run.SideEffect.ActionHash != TenantWorkspaceMutationContract.ActionHash(run.SessionId, operation.ParametersHash)))
            throw new SecurityConflictException("operation_mismatch");
        return args;
    }
    public static async ValueTask RequireAuthority(TenantSecurityState state, AgentRun run, TenantWorkspaceMutationArguments args, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(run, args))
        {
            if (!(await iam.CheckOwnedTenantAsync(state, run.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException(key.EndsWith(".read", StringComparison.Ordinal) ? "executor_read_required" : "permission_denied");
            var policy = TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key));
            if (policy.Mode == OperationApprovalMode.Always || policy.Mode == OperationApprovalMode.WhenRequesterLacksPermission
                && (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is not { } human
                    || !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)) throw new AccessDeniedException("workspace_mutation_review_required");
        }
        if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } requester)
            foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(state.Tenant.TenantId, run.SessionId)),
                ("agents.chat", AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D"))) })
                if (!(await iam.CheckOwnedTenantAsync(state, requester, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("requester_unavailable");
    }
    public static TenantWorkspaceDispatch Fence(AgentRun run) => new(run.Owner.TenantId!.Value, run.AgentRunId, run.Owner, run.SessionId, run.SideEffect.ActionHash!, run.SideEffect.ExternalEffect!);
    public static void RequireFence(AgentRun run, TenantWorkspaceDispatch fence)
    {
        var operation = fence.Origin.DirectOperation ?? throw new SecurityConflictException("operation_mismatch");
        if (run.Owner.TenantId != fence.TenantId || run.Owner != fence.Owner || run.SessionId != fence.SessionId || run.AgentRunId != fence.RunId || run.SideEffect.ExternalEffect != fence.Origin
            || run.SideEffect.ActionHash != fence.ActionHash || run.SideEffect.ToolCallId != "operation:" + operation.OperationId.ToString("D")
            || run.SideEffect.Disposition is not (AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            || TenantWorkspaceMutationContract.Parse(run.SessionId, operation.ToolName, operation.ArgumentsJson).ParametersHash != operation.ParametersHash
            || fence.ActionHash != TenantWorkspaceMutationContract.ActionHash(run.SessionId, operation.ParametersHash)) throw new SecurityConflictException("operation_mismatch");
    }
    public static bool Recoverable(AgentRun run, DateTimeOffset at) => run.SideEffect.Disposition == AgentRunSideEffectDisposition.Indeterminate || run.IsTerminal
        || run.Status != AgentRunStatus.Running || run.Claim is null || run.Claim.Generation != run.SideEffect.ExternalEffect?.Generation || run.Claim.LeaseExpiresAtUtc <= at;
    public static void Outcome(TenantOperationOutcome outcome, string reason, bool recovery)
    {
        if (recovery ? outcome != TenantOperationOutcome.Indeterminate || reason != Uncertain
            : outcome == TenantOperationOutcome.Completed ? reason != Completed : outcome == TenantOperationOutcome.Indeterminate ? reason != Uncertain
            : outcome != TenantOperationOutcome.Denied || reason != "workspace_mutation_not_applied") throw new ArgumentException("Invalid workspace effect result.");
    }
    public static TenantOperationReceipt Receipt(AgentRun run, TenantWorkspaceDispatch fence, TenantOperationOutcome outcome, string reason, DateTimeOffset at)
    {
        var op = fence.Origin.DirectOperation!;
        return new(op.OperationId, null, fence.TenantId, run.AgentRunId, run.SessionId, run.Admission.ExecutionContext!.RootActivationId, run.Owner.ExecutorPrincipalId!.Value,
            run.AgentInstanceId, fence.ActionHash, outcome, reason, op.InstanceRevision, op.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), op.SourceCallId, op.ParametersHash);
    }
    public static TenantOperationReceipt Match(TenantOperationReceipt receipt, AgentRun run, string call, string hash)
    {
        TenantLocalEffectRules.MatchDirectReceipt(receipt, run, call, hash);
        if (receipt.ActionHash != TenantWorkspaceMutationContract.ActionHash(run.SessionId, hash)) throw new SecurityConflictException("operation_mismatch");
        Outcome(receipt.Outcome, receipt.ReasonCode, false); return receipt;
    }
    public static void Transaction(TenantSecurityState next, long revision, AgentRun run, AuthorizationAudit audit, TenantWorkspaceMutationArguments args, bool dispatch)
    {
        var primary = args.Targets.First(t => t.Permission != "workspaces.read"); var scope = TenantWorkspaceRules.Scope(Owner(run, primary.Path));
        if (next.Tenant.TenantId != run.Owner.TenantId || next.Tenant.Revision != checked(revision + 1) || audit.TenantId != next.Tenant.TenantId
            || audit.ActorPrincipalId != run.Owner.ExecutorPrincipalId || audit.Target != scope.ResourceId || audit.Action != (dispatch ? Dispatch : Result))
            throw new ArgumentException("Invalid workspace effect transaction.");
    }
    public static AuthorizationAudit Audit(AuthorizationAudit audit, TenantSecurityState state, AgentRun run, TenantWorkspaceMutationArguments args, Guid operation, bool dispatch)
    {
        var primary = args.Targets.First(t => t.Permission != "workspaces.read");
        return audit with { Execution = new(run.Admission.ExecutionContext!, run.Revision, state.Tenant.Revision, run.Claim?.Generation, primary.Permission,
            TenantWorkspaceRules.Scope(Owner(run, primary.Path)), null, dispatch ? ExecutionAuthorizationPurpose.EffectEligibility : ExecutionAuthorizationPurpose.EffectResult, null, operation, null) };
    }
}
