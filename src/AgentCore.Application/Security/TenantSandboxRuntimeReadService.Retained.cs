using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantSandboxRuntimeReadService
{
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && m.Name is ToolCatalog.RequestAuthorization or ToolCatalog.SandboxProfiles or ToolCatalog.SandboxStatus or ToolCatalog.SandboxProcessStatus))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            var tool = message.Name;
            TenantSandboxRetainedSources.Source? consent = null;
            if (tool == ToolCatalog.RequestAuthorization)
            {
                if (!root.TryGetProperty("inspection", out var inspection) || inspection.ValueKind != JsonValueKind.Object) continue;
                if (approvalStore is null || effectStore is null || operations is null || !root.TryGetProperty("operationId", out var id) || !id.TryGetGuid(out var operationId))
                    throw new AccessDeniedException("sandbox_source_unavailable");
                consent = await TenantSandboxRetainedSources.ReadAsync(run, operationId, operations, approvalStore, effectStore, ct);
                var value = consent.Request.Operation.SandboxRead ?? throw new AccessDeniedException("sandbox_source_unavailable");
                tool = TenantSandboxReadOperationRules.Tool(value.Kind); root = inspection;
                if (consent.Receipt.ReasonCode != TenantSandboxReadOperationRules.Reason) throw new AccessDeniedException("sandbox_source_unavailable");
            }
            else if (tool is not (ToolCatalog.SandboxProfiles or ToolCatalog.SandboxStatus or ToolCatalog.SandboxProcessStatus)) continue;
            if (!root.TryGetProperty("observationOnly", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            var read = await ReadContext(new(run.Owner.TenantId!.Value, run.AgentRunId), tool!, ct, retained: true);
            var profileIds = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                foreach (var profile in profiles.EnumerateArray()) profileIds.Add(profile.GetProperty("id").GetString()!);
            foreach (var field in new[] { "environment", "execution" })
                if (root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("profileId", out var profileId) && profileId.ValueKind == JsonValueKind.String) profileIds.Add(profileId.GetString()!);
            await RequireRead(read.Security, read.Session.Owner, read.Agent.PrincipalId, profileIds.ToArray(), ct);
            if (consent is not null)
            {
                if (profileIds.Any(id => !TenantApprovalRules.ReadProfiles(consent.Request.Operation.SandboxRead!).Any(p => p.Id == id)))
                    throw new AccessDeniedException("sandbox_source_unavailable");
                await RequireInspectionReviewerAsync(consent.Request, read.Security, consent.Decision.HumanPrincipalId, ct);
            }
            else
                foreach (var target in ReadTargets(read.Session.Owner, profileIds))
                    if (await TenantSandboxReadOperationRules.NeedsReviewAsync(read.Security, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId,
                        target.Permission, target.Scope, authorizer, ct)) throw new AccessDeniedException("sandbox_read_review_required");
            await TenantSandboxRetainedSources.FenceAsync(run, read.Security.Tenant.Revision, operations!, ct);
        }
    }
}
