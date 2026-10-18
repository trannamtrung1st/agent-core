using System.Text.Json;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Security;

public sealed class TenantToolAuthorization(ITenantRunStore runs, TenantOperationAuthorizer authorizer,
    TenantApprovalService? approvals = null, TenantRunConfigurationResolver? configuration = null,
    TenantPreferenceOperationService? preferences = null, TenantSandboxRuntimeReadService? sandboxReads = null)
{
    // Legacy previews may read provider data before ExecuteAsync. They cannot prepare tenant authority.
    public async ValueTask<string?> PreparationErrorAsync(Guid session, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (admission?.TenantId is not null || await runs.ResolveTenantSessionOwnerAsync(session, ct).ConfigureAwait(false) is not null
            || admission?.OwnedSessionId is { } owned && owned != session
                && await runs.ResolveTenantSessionOwnerAsync(owned, ct).ConfigureAwait(false) is not null)
            return Error("forbidden", "tenant_effect_unavailable");
        return null;
    }

    private sealed record Boundary(bool Tenant, bool Allowed, TenantRunReference? Reference, string Reason);
    private async ValueTask<Boundary> Resolve(Guid session, ToolExecutionAdmission? admission, string tool, CancellationToken ct)
    {
        var owner = await runs.ResolveTenantSessionOwnerAsync(session, ct).ConfigureAwait(false);
        if (owner is null) return new(false, false, null, "tenant_context_required");
        if (admission is not { AgentRunId: { } runId, SupportsTools: true } || admission.TenantId != owner.TenantId
            || admission.AgentInstanceId != owner.AgentInstanceId || admission.OwnedSessionId != owner.SessionId)
            return new(true, false, null, "tenant_context_required");
        var reference = new TenantRunReference(owner.TenantId, runId);
        try
        {
            var (run, _) = await authorizer.ResolveAsync(reference, ct).ConfigureAwait(false);
            if (run.SessionId != session || run.Owner.ExecutorPrincipalId != owner.ExecutorPrincipalId) return new(true, false, null, "tenant_context_mismatch");
            var allowed = IamToolContracts.IsInspection(tool) || (tool == ToolCatalog.RequestAuthorization || tool == TenantPreferenceToolContract.Name || TenantSandboxInspectionContract.IsInspection(tool))
                && run.Status == AgentCore.Domain.Conversation.AgentRunStatus.Running && run.Claim?.Generation == admission.AgentRunGeneration;
            return new(true, allowed, reference, allowed ? "allowed" : "tenant_effect_unavailable");
        }
        catch (AccessDeniedException ex) { return new(true, false, reference, ex.ReasonCode); }
    }
    public async ValueTask<ToolPolicyDecision?> PolicyAsync(Guid session, ModelToolCall call, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        if (boundary.Tenant && !boundary.Allowed && boundary.Reference is { } failed)
            await authorizer.RecordFailureAsync(failed, "", boundary.Reason, AgentCore.Domain.Security.ExecutionAuthorizationPurpose.ToolBoundary, ct, session).ConfigureAwait(false);
        return boundary.Tenant || IsTenantTool(call.Name) || admission?.TenantId is not null
            ? boundary.Allowed ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny : null;
    }
    public async ValueTask<string?> ExecuteAsync(Guid session, ModelToolCall call, ToolExecutionAdmission? admission, CancellationToken ct, int remainingBytes = ToolLimits.MaxOutputBytes)
    {
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        if (!boundary.Tenant && !IsTenantTool(call.Name) && admission?.TenantId is null) return null;
        if (!boundary.Allowed)
        {
            if (boundary.Tenant && boundary.Reference is { } failed)
                await authorizer.RecordFailureAsync(failed, "", boundary.Reason, AgentCore.Domain.Security.ExecutionAuthorizationPurpose.ToolBoundary, ct, session).ConfigureAwait(false);
            return Error("forbidden", boundary.Reason);
        }
        if (call.Name == ToolCatalog.RequestAuthorization) return Error("invalid", "authorization_requires_runtime_suspension");
        try
        {
            if (TenantSandboxInspectionContract.IsInspection(call.Name))
            {
                if (sandboxReads is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var current = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await sandboxReads.InspectForRunAsync(new(current.Owner, session, boundary.Reference.RunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (call.Name == TenantPreferenceToolContract.Name)
            {
                if (preferences is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var result = await preferences.ExecuteAsync(boundary.Reference!, generation, call.Id, call.ArgumentsJson, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { decision = JsonNamingPolicy.CamelCase.ConvertName(result.Decision.ToString()),
                    reasonCode = result.ReasonCode, applied = result.Receipt is not null, operationId = result.Receipt?.OperationId,
                    instanceRevision = result.Receipt?.InstanceRevision });
            }
            if (string.IsNullOrWhiteSpace(call.ArgumentsJson) || Encoding.UTF8.GetByteCount(call.ArgumentsJson) > IamToolContracts.MaxArgumentBytes)
                return Error("invalid", "invalid_arguments");
            using var document = JsonDocument.Parse(call.ArgumentsJson); var input = document.RootElement;
            var check = call.Name == ToolCatalog.IamCheckAccess;
            if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any(p => p.Name is not ("resourceType" or "resourceId") && (!check || p.Name != "permission")))
                return Error("invalid", "invalid_arguments");
            if (input.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != input.EnumerateObject().Count()) return Error("invalid", "invalid_arguments");
            string Read(string name, int max) => input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text && text.Length <= max ? text : throw new ArgumentException("Invalid IAM arguments.");
            var type = Read("resourceType", 32); var id = Read("resourceId", 200);
            if (check) return JsonSerializer.Serialize(Project(await authorizer.CheckAsync(boundary.Reference!, Read("permission", 128), type, id, ct).ConfigureAwait(false)));
            var items = await authorizer.EffectiveAsync(boundary.Reference!, type, id, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { operations = items.Select(Project).ToArray() });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return Error("invalid", "invalid_arguments"); }
        catch (AccessDeniedException ex) { return Error("forbidden", ex.ReasonCode); }
        catch (SecurityConflictException ex) { return Error("conflict", ex.ReasonCode); }
    }
    public async ValueTask<TenantApprovalPreparationResult> PrepareAsync(Guid session, ModelToolCall call, ToolExecutionAdmission admission, CancellationToken ct)
    {
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        if (!boundary.Tenant || !boundary.Allowed || call.Name != ToolCatalog.RequestAuthorization || boundary.Reference is null || approvals is null || configuration is null)
            throw new AccessDeniedException("tenant_context_required");
        var intent = TenantApprovalToolContract.Parse(call);
        var run = (await runs.FindTenantRunAsync(boundary.Reference.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false))!;
        if (run.Status != AgentCore.Domain.Conversation.AgentRunStatus.Running || run.Claim?.Generation != admission.AgentRunGeneration)
            throw new AccessDeniedException("tenant_runtime_unavailable");
        if (intent.SandboxRead is { } read)
            return await approvals.PrepareSandboxReadAsync(boundary.Reference, read.Kind, read.ExecutionId, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.SandboxControl is { } control)
            return await approvals.PrepareSandboxControlAsync(boundary.Reference, control.Kind, control.ExecutionId, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.Sandbox is { } sandbox)
            return await approvals.PrepareSandboxAsync(boundary.Reference, sandbox.ProfileId, sandbox.Command, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.CredentialReference is { } reference)
            return await approvals.PrepareCredentialCheckAsync(boundary.Reference, reference, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        var current = await configuration.ResolveAsync(boundary.Reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (TenantLocalEffectRules.Eligibility(run, current.Agent, current.Configuration.Configuration.Definition) is { } denied)
            throw new AccessDeniedException(denied);
        return intent.Preferences is { } preferences
            ? await approvals.PreparePreferencesAsync(boundary.Reference, preferences, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false)
            : await approvals.PreparePersonaAsync(boundary.Reference, intent.Persona!, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
    }
    private static bool IsTenantTool(string name) => IamToolContracts.IsInspection(name) || name is ToolCatalog.RequestAuthorization or TenantPreferenceToolContract.Name;
    private static object Project(OperationAuthorizationDecision decision) => new { decision = JsonNamingPolicy.CamelCase.ConvertName(decision.Decision.ToString()),
        reasonCode = decision.ReasonCode, requiredPermission = decision.Permission, targetResource = new { type = decision.Resource.ResourceType, id = decision.Resource.ResourceId },
        approvalKind = decision.ApprovalKind is { } kind ? JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()) : null };
    private static string Error(string error, string reason) => JsonSerializer.Serialize(new { error, reasonCode = reason });
}
