using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantBrowserEffectService
{
    // Image payloads are rebuilt under current independent Artifact read authority; never persisted in a checkpoint.
    public async ValueTask<List<ModelMessage>> RehydrateCapturesAsync(AgentRun run, List<ModelMessage> messages, bool vision, CancellationToken ct = default)
    {
        var result = messages.ToList();
        for (var index = 0; index < result.Count; index++)
        {
            var message = result[index];
            if (message.Role != ModelRole.Tool || message.Name != ToolCatalog.RequestAuthorization) continue;
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (!root.TryGetProperty("requestId", out var requestValue) || !requestValue.TryGetGuid(out var requestId)
                || !root.TryGetProperty("operationId", out var operationValue) || !operationValue.TryGetGuid(out var operationId)) continue;
            var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
            var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false);
            if (request?.Operation.BrowserCommand?.ToolName != ToolCatalog.BrowserScreenshot) continue;
            message = ImageStatus(message with { Parts = null }, false, vision ? "capture_unavailable" : "vision_unavailable"); result[index] = message;
            if (!vision || artifacts is null) continue;
            var receipt = await effects.FindOperationReceiptAsync(tenant, operationId, ct).ConfigureAwait(false);
            if (receipt?.BrowserArtifactsJson is null || receipt.RequestId != requestId || receipt.OperationId != request.Operation.OperationId
                || receipt.Outcome != TenantOperationOutcome.Completed || receipt.TenantId != tenant || receipt.SessionId != run.SessionId
                || receipt.RootActivationId != run.Admission.Activation.RootActivationId || receipt.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId
                || receipt.InstanceId != run.AgentInstanceId || receipt.ActionHash != request.Operation.ActionHash) continue;
            var capture = TenantBrowserArtifactRules.Read(receipt.BrowserArtifactsJson).Files.SingleOrDefault(f => f.Kind == "capture");
            if (capture is null || capture.ArtifactId != operationId) continue;
            async ValueTask<(AgentRun Run, TenantSecurityState State)> Require()
            {
                var (current, state) = await operations.ResolveAsync(new(tenant, run.AgentRunId), ct).ConfigureAwait(false);
                RequireClaim(current, run.Claim!.Generation);
                await targets.RequireObservationSourceAsync(request, current, ct).ConfigureAwait(false);
                if (!(await authorizer.CheckTenantAsync(state, current.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get("sessions.read"),
                    TenantSessionRules.Scope(tenant, current.SessionId), ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("permission_denied");
                var scope = TenantArtifactRules.Scope(tenant, capture.ArtifactId);
                if (!(await authorizer.CheckTenantAsync(state, current.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get("artifacts.read"), scope, ct).ConfigureAwait(false)).Allowed)
                    throw new AccessDeniedException("permission_denied");
                var policy = await operations.Check(state, current, "artifacts.read", scope, ct, purpose: ExecutionAuthorizationPurpose.Inspection).ConfigureAwait(false);
                if (policy.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(policy.Decision == OperationDecisionKind.RequiresApproval
                    ? "artifact_read_review_required" : "permission_denied");
                return (current, state);
            }
            try
            {
                var before = await Require().ConfigureAwait(false);
                var item = await artifacts.FindTenantAsync(tenant, run.SessionId, capture.ArtifactId, ct).ConfigureAwait(false);
                if (item is null || item.AgentRunId != receipt.RunId || item.ByteSize is <= 0 or > BrowserToolLimits.MaxCaptureBytes
                    || item.ContentType is not ("image/png" or "image/jpeg" or "image/webp")) continue;
                var bytes = await artifacts.ReadTenantContentAsync(tenant, run.SessionId, capture.ArtifactId, ct).ConfigureAwait(false);
                TenantArtifactRules.RequireContent(item, bytes);
                var after = await Require().ConfigureAwait(false);
                if (before.Run.Revision != after.Run.Revision || before.State.Tenant.Revision != after.State.Tenant.Revision) continue;
                result[index] = ImageStatus(message, true, null) with { Parts = [new ModelImageContent(item.ContentType, bytes, item.DisplayName)] };
            }
            catch (Exception e) when (e is AccessDeniedException or SecurityConflictException or IOException or AgentCore.Application.Sessions.AgentCoreException)
            {
                var reason = e is AccessDeniedException denied && denied.ReasonCode is "permission_denied" or "artifact_read_review_required"
                    ? denied.ReasonCode : "capture_unavailable";
                result[index] = ImageStatus(message, false, reason);
            }
        }
        return result;
    }
    public async ValueTask RequireProviderImageSourcesAsync(AgentRun run, IReadOnlyList<ModelMessage> messages,
        Func<CancellationToken, ValueTask>? otherSources, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var before = (await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision ?? throw new AccessDeniedException("tenant_unavailable");
        if (otherSources is not null) await otherSources(ct).ConfigureAwait(false);
        await RequireImageSourcesAsync(run, messages, ct).ConfigureAwait(false);
        if ((await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision != before) throw new AccessDeniedException("browser_source_changed");
    }
    // Fence owned image parts immediately before provider invocation, including provider role projections.
    public async ValueTask RequireImageSourcesAsync(AgentRun run, IReadOnlyList<ModelMessage> messages, CancellationToken ct = default)
    {
        var candidates = messages.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization
            && m.Parts?.OfType<ModelImageContent>().Any() == true).ToArray();
        if (candidates.Length == 0) return;
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var revision = (await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision ?? throw new AccessDeniedException("tenant_unavailable");
        foreach (var message in candidates)
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (!root.TryGetProperty("requestId", out var requestId) || !requestId.TryGetGuid(out var id)
                || !root.TryGetProperty("operationId", out var operationId) || !operationId.TryGetGuid(out var operation)) throw new AccessDeniedException("capture_unavailable");
            var request = await approvals.FindApprovalAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("capture_unavailable");
            if (request.Operation.BrowserCommand?.ToolName != ToolCatalog.BrowserScreenshot) continue; // The Attachment owner fences its own image parts.
            var receipt = await effects.FindOperationReceiptAsync(tenant, operation, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("capture_unavailable");
            if (receipt.RequestId != id || receipt.OperationId != request.Operation.OperationId || receipt.SessionId != run.SessionId
                || receipt.RootActivationId != run.Admission.Activation.RootActivationId || receipt.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId
                || receipt.InstanceId != run.AgentInstanceId || receipt.ActionHash != request.Operation.ActionHash || receipt.BrowserArtifactsJson is null
                || receipt.Outcome != TenantOperationOutcome.Completed || !TenantBrowserArtifactRules.Read(receipt.BrowserArtifactsJson).Files.Any(f => f.Kind == "capture" && f.ArtifactId == operation))
                throw new AccessDeniedException("capture_unavailable");
            var (current, state) = await operations.ResolveAsync(new(tenant, run.AgentRunId), ct).ConfigureAwait(false); RequireClaim(current, run.Claim!.Generation);
            await targets.RequireObservationSourceAsync(request, current, ct).ConfigureAwait(false);
            var scope = TenantArtifactRules.Scope(tenant, operation);
            if (state.Tenant.Revision != revision || !(await authorizer.CheckTenantAsync(state, current.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get("sessions.read"),
                    TenantSessionRules.Scope(tenant, current.SessionId), ct).ConfigureAwait(false)).Allowed
                || !(await authorizer.CheckTenantAsync(state, current.Owner.ExecutorPrincipalId.Value, PermissionCatalog.Get("artifacts.read"), scope, ct).ConfigureAwait(false)).Allowed
                || (await operations.Check(state, current, "artifacts.read", scope, ct, purpose: ExecutionAuthorizationPurpose.Inspection).ConfigureAwait(false)).Decision != OperationDecisionKind.Allowed)
                throw new AccessDeniedException("permission_denied");
            var file = artifacts is null ? null : await artifacts.FindTenantAsync(tenant, current.SessionId, operation, ct).ConfigureAwait(false);
            var images = message.Parts!.OfType<ModelImageContent>().ToArray();
            if (file is null || file.AgentRunId != receipt.RunId || images.Length != 1 || images[0].ContentType != file.ContentType
                || images[0].Bytes.Length != file.ByteSize || images[0].ContentSha256 != file.Sha256Hex) throw new AccessDeniedException("capture_unavailable");
        }
        var final = await runs.FindTenantRunAsync(tenant, run.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable"); RequireClaim(final, run.Claim!.Generation);
        if ((await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision != revision) throw new AccessDeniedException("browser_source_changed");
    }
    private static ModelMessage ImageStatus(ModelMessage message, bool provided, string? reason)
    {
        var value = JsonNode.Parse(message.Text)!.AsObject();
        value["captureImageProvided"] = provided; value["captureImageUnavailableReason"] = reason;
        return message with { Text = value.ToJsonString() };
    }
}
