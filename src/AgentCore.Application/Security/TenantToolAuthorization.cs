using System.Text.Json;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Security;

public sealed class TenantToolAuthorization(ITenantRunStore runs, TenantOperationAuthorizer authorizer,
    TenantApprovalService? approvals = null, TenantRunConfigurationResolver? configuration = null)
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
            var allowed = IamToolContracts.IsInspection(tool) || tool == ToolCatalog.RequestAuthorization
                && run.Status == AgentCore.Domain.Conversation.AgentRunStatus.Running && run.Claim?.Generation == admission.AgentRunGeneration;
            return new(true, allowed, reference, allowed ? "allowed" : "tenant_effect_unavailable");
        }
        catch (AccessDeniedException ex) { return new(true, false, null, ex.ReasonCode); }
    }
    public async ValueTask<ToolPolicyDecision?> PolicyAsync(Guid session, ModelToolCall call, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        return boundary.Tenant || (IamToolContracts.IsInspection(call.Name) || call.Name == ToolCatalog.RequestAuthorization) || admission?.TenantId is not null
            ? boundary.Allowed ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny : null;
    }
    public async ValueTask<string?> ExecuteAsync(Guid session, ModelToolCall call, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        if (!boundary.Tenant && !(IamToolContracts.IsInspection(call.Name) || call.Name == ToolCatalog.RequestAuthorization) && admission?.TenantId is null) return null;
        if (!boundary.Allowed) return Error("forbidden", boundary.Reason);
        if (call.Name == ToolCatalog.RequestAuthorization) return Error("invalid", "authorization_requires_runtime_suspension");
        try
        {
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
    }
    public async ValueTask<TenantApprovalPreparationResult> PreparePersonaAsync(Guid session, ModelToolCall call, ToolExecutionAdmission admission, CancellationToken ct)
    {
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        if (!boundary.Tenant || !boundary.Allowed || call.Name != ToolCatalog.RequestAuthorization || boundary.Reference is null || approvals is null || configuration is null)
            throw new AccessDeniedException("tenant_context_required");
        var persona = TenantApprovalToolContract.Parse(call);
        var run = (await runs.FindTenantRunAsync(boundary.Reference.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false))!;
        if (run.Status != AgentCore.Domain.Conversation.AgentRunStatus.Running || run.Claim?.Generation != admission.AgentRunGeneration)
            throw new AccessDeniedException("tenant_runtime_unavailable");
        var current = await configuration.ResolveAsync(boundary.Reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (TenantPersonaEffectRules.Eligibility(run, current.Agent, current.Configuration.Configuration.Definition) is { } denied)
            throw new AccessDeniedException(denied);
        return await approvals.PreparePersonaAsync(boundary.Reference, persona, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
    }
    private static object Project(OperationAuthorizationDecision decision) => new { decision = JsonNamingPolicy.CamelCase.ConvertName(decision.Decision.ToString()),
        reasonCode = decision.ReasonCode, requiredPermission = decision.Permission, targetResource = new { type = decision.Resource.ResourceType, id = decision.Resource.ResourceId },
        approvalKind = decision.ApprovalKind is { } kind ? JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()) : null };
    private static string Error(string error, string reason) => JsonSerializer.Serialize(new { error, reasonCode = reason });
}
