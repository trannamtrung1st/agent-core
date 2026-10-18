using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantBrowserReadService(ITenantRunStore runs, ITenantSessionStore sessions, IIamStore security,
    TenantOperationAuthorizer operations, IamAuthorizer authorizer, TenantRunConfigurationResolver configuration,
    TenantBrowserOperationTarget targets, IBrowser browser, TimeProvider time)
{
    private sealed record Read(AgentRun Run, TenantSession Session, TenantAgent Agent, TenantSecurityState State, BrowserCommand Command);
    public async ValueTask<string> InspectAsync(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct = default)
    {
        try { return await InspectCoreAsync(frame, call, remainingBytes, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            if (frame.Owner.TenantId is { } tenant)
                await operations.RecordFailureAsync(new(tenant, frame.RunId), "browser.read", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                    ExecutionAuthorizationPurpose.ToolBoundary, ct, frame.SessionId).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<string> InspectCoreAsync(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct)
    {
        var started = time.GetTimestamp(); call = TenantBrowserInspectionContract.Normalize(call);
        var before = await RequireAsync(frame, call, ct, audit: true).ConfigureAwait(false);
        var prepared = await targets.PrepareAsync(before.Run, call.Name, call.ArgumentsJson, ct, registerCold: true).ConfigureAwait(false);
        var duration = before.Run.Claim!.LeaseExpiresAtUtc - time.GetUtcNow() - TimeSpan.FromSeconds(1);
        var tool = TimeSpan.FromSeconds(Math.Min(30, before.Run.Admission.ExecutionBudget?.Profile.PerToolSeconds ?? 30)) - time.GetElapsedTime(started);
        if (duration > tool) duration = tool;
        if (before.Run.Checkpoint?.PauseBudgetClock(time.GetUtcNow()).RemainingOverallBudgetMs is { } overall && duration.TotalMilliseconds > overall)
            duration = TimeSpan.FromMilliseconds(overall);
        if (duration <= TimeSpan.Zero) throw new AccessDeniedException("execution_budget_exhausted");
        using var deadline = new CancellationTokenSource(duration, time); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var pending = browser.ExecuteAsync(new(frame.SessionId, before.Command) { TenantOwner = prepared.Witness.Owner, TenantWitness = prepared.Witness,
            TenantReadOnly = true, TenantDispatchCheck = async token => {
                try { await FenceAsync(frame, call, before, token).ConfigureAwait(false); return null; }
                catch (AccessDeniedException e) { return Safe(e.ReasonCode); }
                catch (SecurityConflictException) { return "browser_source_changed"; }
            } }, linked.Token).AsTask();
        BrowserResult result;
        try { result = await pending.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch { _ = ObserveAsync(pending); throw; }
        ct.ThrowIfCancellationRequested(); linked.Token.ThrowIfCancellationRequested();
        await FenceAsync(frame, call, before, ct).ConfigureAwait(false);
        if (result.EffectAttempted) throw new AccessDeniedException("browser_read_only");
        if (result.ErrorCode is { } error) return ToolJsonResults.FitToBudget(remainingBytes, JsonSerializer.Serialize(new { error = Safe(error), readAuthorized = true, observationUnavailable = true }));
        var bounded = JsonNode.Parse(TenantBrowserContinuationProjection.Observation(result))!.AsObject();
        var data = bounded["observation"] as JsonObject ?? bounded["data"] as JsonObject ?? new JsonObject { ["status"] = bounded["status"]?.DeepClone() };
        data = (JsonObject)data.DeepClone();
        if (result.Observation is { } observed)
        {
            data["status"] = "ok"; data["truncated"] = data["contentTruncated"]?.DeepClone() ?? false;
            data["hasMore"] = data["truncated"]?.DeepClone(); data["guidance"] = BrowserToolArguments.TargetGuidance;
            if (observed.Intervention != BrowserInterventionKind.None)
            {
                data = new JsonObject { ["error"] = "user_intervention_required", ["kind"] = observed.Intervention.ToString(),
                    ["url"] = data["url"]?.DeepClone(), ["title"] = data["title"]?.DeepClone(), ["message"] = "Complete this step in the browser, then ask to continue." };
            }
        }
        data["untrustedBrowserContent"] = true; data["readAuthorized"] = true; data["tenantBrowserRead"] = true;
        data["readCommandHash"] = BrowserCommandIdentity.Hash(before.Command); data["applicationOutcomeVerified"] = false;
        data["effectAttempted"] = false; data["effectConfirmedBySdk"] = false;
        var text = data.ToJsonString();
        return ToolJsonResults.FitToBudget(remainingBytes, text);
    }
    private async ValueTask<Read> RequireAsync(TenantRuntimeFrame frame, ModelToolCall call, CancellationToken ct, bool audit = false)
    {
        var command = TenantBrowserInspectionContract.Parse(frame.SessionId, call);
        var (run, state) = await operations.ResolveAsync(new(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(state.Tenant.TenantId, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (run.SideEffect.ExternalEffect is not null && run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new AccessDeniedException("browser_effect_in_progress");
        var (agent, baseline) = await configuration.BaselineAsync(state.Tenant.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        TenantBrowserOperationRules.RequireSource(frame.SessionId, call.Name, run, agent, baseline, session);
        foreach (var (permission, scope) in TenantBrowserOperationRules.Targets(state.Tenant.TenantId, run.AgentInstanceId, frame.SessionId, true))
        {
            if (!(await authorizer.CheckTenantAsync(state, agent.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
            var decision = audit ? await operations.Check(state, run, permission, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(state, run.Admission.ExecutionContext!, permission, scope, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.Allowed)
                throw new AccessDeniedException(decision.Decision == OperationDecisionKind.RequiresApproval ? "browser_read_review_required" : decision.ReasonCode);
        }
        return new(run, session, agent, state, command);
    }
    private async ValueTask FenceAsync(TenantRuntimeFrame frame, ModelToolCall call, Read before, CancellationToken ct)
    {
        var after = await RequireAsync(frame, call, ct).ConfigureAwait(false);
        if (after.Run.Revision != before.Run.Revision || after.State.Tenant.Revision != before.State.Tenant.Revision || after.Session.Owner != before.Session.Owner
            || after.Session.Snapshot.Revision != before.Session.Snapshot.Revision || after.Agent.Instance.Revision != before.Agent.Instance.Revision)
            throw new SecurityConflictException("browser_source_changed");
        if ((await security.ReadTenantAsync(before.State.Tenant.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != before.State.Tenant.Revision)
            throw new SecurityConflictException("browser_source_changed");
        var final = await runs.FindTenantRunAsync(before.State.Tenant.TenantId, frame.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        TenantRuntimeRules.Require(frame, after.Session.Owner, final, time.GetUtcNow());
        if (final.Revision != after.Run.Revision) throw new SecurityConflictException("browser_source_changed");
    }
    public async ValueTask RequireStoredObservationsAsync(AgentRun run, CancellationToken ct = default)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        IReadOnlyList<ModelToolCall> batch = []; var reads = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        foreach (var message in messages!)
        {
            if (message.Role == ModelRole.Assistant && message.ToolCalls is { } calls) batch = calls;
            if (message.Role != ModelRole.Tool || !TenantBrowserInspectionContract.IsInspection(message.Name ?? "")) continue;
            using var json = JsonDocument.Parse(message.Text); var root = json.RootElement;
            if (!root.TryGetProperty("tenantBrowserRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            var call = batch.SingleOrDefault(c => c.Id == message.ToolCallId && c.Name == message.Name) ?? throw new AccessDeniedException("browser_source_changed");
            var command = TenantBrowserInspectionContract.Parse(run.SessionId, call); var hash = BrowserCommandIdentity.Hash(command);
            if (!root.TryGetProperty("readCommandHash", out var expected) || expected.GetString() != hash) throw new AccessDeniedException("browser_source_changed");
            reads.TryAdd(hash, call);
        }
        foreach (var call in reads.Values)
        {
            var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
            var read = await RequireAsync(frame, call, ct).ConfigureAwait(false); await FenceAsync(frame, call, read, ct).ConfigureAwait(false);
        }
    }
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal) { "permission_denied", "browser_read_review_required", "browser_source_changed", "browser_effect_in_progress",
        "tenant_runtime_unavailable", "requester_unavailable", "executor_unavailable", "resource_unavailable", "provider_unavailable", "policy_transition_pending", "browser_target_changed",
        "target_denied", "target_missing", "invalid", "invalid_frame", "stale_frame", "stale_tab", "dialog_pending", "dialog_missing", "unsupported_operation", "timeout", "not_found", "ambiguous_target" };
    private static string Safe(string reason) => Codes.Contains(reason) ? reason : "browser_read_unavailable";
    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch (Exception) { } }
}
