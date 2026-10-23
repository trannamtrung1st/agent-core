using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantPublicWebReadRules
{
    public static IReadOnlyList<string> StoredTools(AgentRun run)
    {
        try
        {
            if (!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint,out var messages)) return [];
            IReadOnlyList<ModelToolCall> batch=[]; var reads=new HashSet<string>(StringComparer.Ordinal);
            foreach(var message in messages!)
            {
                if(message.Role==ModelRole.Assistant && message.ToolCalls is { } calls)batch=calls;
                if(message.Role!=ModelRole.Tool || !TenantPublicWebReadContract.IsRead(message.Name ?? ""))continue;
                using var json=JsonDocument.Parse(message.Text); var root=json.RootElement;
                var call=batch.SingleOrDefault(c=>c.Id==message.ToolCallId && c.Name==message.Name) ?? throw new AccessDeniedException("public_web_source_changed");
                if (CoreError(root, call)) continue;
                var args=TenantPublicWebReadContract.Parse(call.Name,call.ArgumentsJson);
                if(!root.TryGetProperty("tenantPublicWebRead",out var marker) || marker.ValueKind!=JsonValueKind.True
                    || !root.TryGetProperty("readCommandHash",out var hash) || hash.GetString()!=args.Hash)throw new AccessDeniedException("public_web_source_changed");
                reads.Add(call.Name);
            }
            return reads.ToArray();
        }
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException) { throw new AccessDeniedException("public_web_source_changed"); }
    }
    private static bool CoreError(JsonElement root, ModelToolCall call)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String || !ErrorCodes.Contains(error.GetString()!)
            || root.EnumerateObject().Any(p => p.Name is not ("error" or "reasonCode" or "message" or "untrusted" or "truncated" or "invalidCallRecovery"))) return false;
        if (root.TryGetProperty("reasonCode", out var reason) && (reason.ValueKind != JsonValueKind.String || !Reasons.Contains(reason.GetString()!))) return false;
        if (root.TryGetProperty("message", out var message) && (message.ValueKind != JsonValueKind.String || message.GetString() is not
            ("Tool is not permitted for this role." or "Correct arguments or report the blocked strategy."))) return false;
        if (root.TryGetProperty("untrusted", out var untrusted) && untrusted.ValueKind != JsonValueKind.True) return false;
        if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        return !root.TryGetProperty("invalidCallRecovery", out var recovery) || InvalidToolCallRecovery.IsReceiptMetadata(call, recovery);
    }
    private static readonly HashSet<string> ErrorCodes = new(StringComparer.Ordinal) {
        "invalid", "forbidden", "conflict", "output_limit", "invalid_tool_strategy_blocked", "public_web_unavailable", "timeout", "forbidden_host", "credentials_in_url",
        "invalid_url", "http_error", "body_too_large", "unsupported_content_type", "too_many_redirects", "transport_error", "unavailable" };
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal) {
        "invalid_arguments", "executor_read_required", "public_web_read_review_required", "permission_denied", "public_web_unavailable", "provider_unavailable",
        "tenant_context_required", "tenant_context_mismatch", "tenant_runtime_unavailable", "resource_unavailable", "run_unavailable", "public_web_source_changed",
        "stale_revision", "execution_budget_exhausted", "requester_unavailable", "executor_unavailable", "tenant_unavailable", "policy_transition_pending", "capability_unavailable" };
    public static void Require(AgentRun run,TenantAgent agent,AgentDefinition current,TenantSession session,IReadOnlyList<string> tools)
    {
        if(run.Owner.TenantId is not { } tenant || agent.TenantId!=tenant || agent.PrincipalId!=run.Owner.ExecutorPrincipalId || agent.Instance.InstanceId!=run.AgentInstanceId
            || session.Owner.TenantId!=tenant || session.Owner.SessionId!=run.SessionId || session.Owner.AgentInstanceId!=run.AgentInstanceId || session.Owner.ExecutorPrincipalId!=agent.PrincipalId
            || agent.Instance.Lifecycle!=AgentInstanceLifecycle.Active || !TenantTaskExecutionContract.Allows(run,current)
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus is not (SessionLifecycleStatus.Active or SessionLifecycleStatus.Completed)
            || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended
            || tools.Any(t=>!TenantPublicWebReadContract.Allows(run.Admission.Configuration!.Definition,t) || !TenantPublicWebReadContract.Allows(current,t)))
            throw new AccessDeniedException("public_web_source_changed");
    }
    public static (string Permission,AccessScope Scope)[] Targets(AgentRun run) => [
        ("agents.read",AccessScope.ForResource(run.Owner.TenantId!.Value,"agent",run.AgentInstanceId.ToString("D"))),
        ("sessions.read",TenantSessionRules.Scope(run.Owner.TenantId.Value,run.SessionId))];
    public static async ValueTask PermissionsAsync(TenantSecurityState state,AgentRun run,TenantAgent agent,IamAuthorizer iam,CancellationToken ct)
    {
        foreach(var (permission,scope) in Targets(run))
        {
            if(!(await iam.CheckOwnedTenantAsync(state,agent.PrincipalId,PermissionCatalog.Get(permission),scope,ct).ConfigureAwait(false)).Allowed
                || await TenantSandboxReadOperationRules.NeedsReviewAsync(state,run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,permission,scope,iam,ct).ConfigureAwait(false))
                throw new AccessDeniedException("public_web_output_unavailable");
        }
    }
}
