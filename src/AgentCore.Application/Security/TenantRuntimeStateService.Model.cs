using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantRuntimeStateService
{
    private readonly Dictionary<Guid, DateTimeOffset> refresh = [];
    private sealed record ModelRead(AgentRun Run, TenantSecurityState Security, TenantAgent Agent, TenantSession Session);
    public async ValueTask<string> InspectForRunAsync(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct = default)
    {
        if (call.Name != ToolCatalog.AgentStateInspect || string.IsNullOrWhiteSpace(call.ArgumentsJson) || System.Text.Encoding.UTF8.GetByteCount(call.ArgumentsJson) > 1024)
            throw new ArgumentException("Use bounded state inspection arguments.");
        using var document = JsonDocument.Parse(call.ArgumentsJson); var args = document.RootElement;
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => p.Name != "scope") || args.EnumerateObject().Count() > 1
            || args.TryGetProperty("scope", out var value) && (value.ValueKind != JsonValueKind.String || value.GetString() is not ("currentSession" or "instanceSummary")))
            throw new ArgumentException("Use only optional scope currentSession or instanceSummary; owner and resource IDs are not arguments.");
        var scope = args.TryGetProperty("scope", out value) ? value.GetString()! : "currentSession";
        var result = await ObserveForRunAsync(frame, scope, true, ct).ConfigureAwait(false);
        return ToolJsonResults.FitToBudget(Math.Min(remainingBytes, 4096), JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    public async ValueTask<RuntimeStateObservation> ObserveForRunAsync(TenantRuntimeFrame frame, string scope = "currentSession", bool rateLimit = true, CancellationToken ct = default)
    {
        if (scope is not ("currentSession" or "instanceSummary")) throw AgentCoreErrors.Validation("Unsupported state visibility scope.");
        var before = await RequireModel(frame, scope, ct).ConfigureAwait(false);
        if (rateLimit)
        {
            var now = time.GetUtcNow(); lock (refresh)
            {
                if (refresh.TryGetValue(frame.RunId, out var previous) && now - previous < TimeSpan.FromSeconds(1))
                    throw new AgentCoreException("state_refresh_limited", "Wait one second before refreshing state again.", 429);
                if (refresh.Count >= 512) refresh.Remove(refresh.MinBy(p => p.Value).Key);
                refresh[frame.RunId] = now;
            }
        }
        async ValueTask<bool> Can(string permission, AccessScope target) =>
            (await operations.Check(before.Security, before.Run, permission, target, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)).Decision == OperationDecisionKind.Allowed;
        var principals = new[] { before.Agent.PrincipalId, before.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId }.OfType<Guid>().Distinct().ToArray();
        var result = await Observe(before.Security, before.Agent, principals, Can, scope == "currentSession" ? frame.SessionId : null, frame.RunId, ct).ConfigureAwait(false);
        var after = await RequireModel(frame, scope, ct).ConfigureAwait(false);
        if (after.Security.Tenant.Revision != before.Security.Tenant.Revision || after.Agent.Instance.Revision != before.Agent.Instance.Revision
            || after.Session.Owner != before.Session.Owner) throw new SecurityConflictException("stale_revision");
        return result with { Scope = scope };
    }
    private async ValueTask<ModelRead> RequireModel(TenantRuntimeFrame frame, string scope, CancellationToken ct)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        var (agent, baseline) = await configuration.BaselineAsync(tenant, frame.Owner.AgentInstanceId, ct).ConfigureAwait(false);
        var current = InstanceSettingsResolver.ResolveForAgent(baseline, agent);
        var admitted = run.Admission.Configuration?.Definition;
        if (admitted is null || !TenantTaskExecutionContract.Allows(run, current)
            || !RolePermissions.AllowsTool(admitted, ToolCatalog.AgentStateInspect) || !RolePermissions.AllowsTool(current, ToolCatalog.AgentStateInspect))
            throw new AccessDeniedException("state_inspection_unavailable");
        var visibility = StateIsolationPolicy.Restrict(admitted.StateIsolationPolicy ?? StateIsolationPolicy.Default, current.StateIsolationPolicy ?? StateIsolationPolicy.Default).AgentStateVisibility;
        if (visibility == AgentStateVisibility.Off || scope == "instanceSummary" && visibility != AgentStateVisibility.InstanceSummary
            || session.Snapshot.ArchivedAt is not null || session.Snapshot.DurablyDeletedAt is not null || SessionLifecycle.IsTerminal(session.Snapshot.LifecycleStatus)
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active) throw new AccessDeniedException("state_inspection_unavailable");
        return new(run, state, agent, session);
    }
}
