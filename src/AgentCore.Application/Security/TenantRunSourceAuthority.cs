using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
namespace AgentCore.Application.Security;

// The same retained private-source checks are required before model reentry,
// completion inspection and report delivery. Their existing owners perform each check.
public sealed class TenantRunSourceAuthority(TenantRunProfileService profiles, TenantMemoryService memories,
    TenantContinuityReadService continuity, TenantAutomationReadService automations, TenantManagementReadService management, TenantComposerReferenceService? composer = null,
    TenantWorkspaceRuntimeReadService? workspace = null, TenantArtifactRuntimeReadService? artifacts = null,
    TenantBrowserReadService? browser = null, TenantBrowserEffectService? browserEffects = null, TenantSandboxRuntimeReadService? sandboxReads = null, TenantSandboxApprovedOperationService? sandboxEffects = null)
{
    public ValueTask<AgentCore.Application.Ports.TenantSkillProjection> SkillContextAsync(AgentRun run, CancellationToken ct) => management.SkillContextAsync(run, ct);
    public ValueTask<(AgentCore.Application.Ports.TenantManagementReadObservation Observation, OperationAuthorizationDecision Decision)> SkillLoadAsync(AgentRun run, IReadOnlyList<string> ids, CancellationToken ct) => management.SkillLoadAsync(run, ids, ct);
    public static bool HasSources(AgentRun run, bool checkProfile = true) => run.Admission.ComposerInput is not null || checkProfile && AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource is not null
        || TenantManagementReadReferences.Read(run.Checkpoint).Count > 0 || TenantMemoryOperationReferences.Read(run.Checkpoint).Count > 0 || TenantAutomationReadReferences.Read(run.Checkpoint).Count > 0
        || TenantExperienceReferences.Sources(run.Checkpoint).Count > 0 || TenantExperienceReferences.Facts(run.Checkpoint).Count > 0 || TenantHistoricalReferences.Read(run.Checkpoint).Count > 0
        || NativeSources(run) != 0;
    private static int NativeSources(AgentRun run)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return 0;
        var flags = 0;
        foreach (var message in messages!.Where(m => m.Role == AgentCore.Application.Ports.ModelRole.Tool))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(message.Text);
                var root = json.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                bool Mark(System.Text.Json.JsonElement value, string name) => value.ValueKind == System.Text.Json.JsonValueKind.Object
                    && value.TryGetProperty(name, out var marker) && marker.ValueKind == System.Text.Json.JsonValueKind.True;
                if (Mark(root, "tenantWorkspaceRead") || root.TryGetProperty("workspaceObservation", out var observed) && Mark(observed, "tenantWorkspaceRead")) flags |= 1;
                if (Mark(root, "tenantArtifactRead") || root.TryGetProperty("artifactMetadata", out var metadata) && Mark(metadata, "tenantArtifactRead")) flags |= 2;
                if (Mark(root, "tenantBrowserRead")) flags |= 4;
                if (message.Name is AgentCore.Application.Tools.ToolCatalog.SandboxProfiles or AgentCore.Application.Tools.ToolCatalog.SandboxStatus or AgentCore.Application.Tools.ToolCatalog.SandboxProcessStatus
                    && Mark(root, "observationOnly") || root.TryGetProperty("inspection", out var inspection) && Mark(inspection, "observationOnly")) flags |= 16;
                if (message.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization && root.TryGetProperty("sandbox", out var sandbox) && sandbox.ValueKind == System.Text.Json.JsonValueKind.Object) flags |= 32;
                if (message.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization
                    && root.TryGetProperty("browser", out var effect) && effect.ValueKind != System.Text.Json.JsonValueKind.Null) flags |= 8;
            }
            catch (System.Text.Json.JsonException) { } // Ordinary text/error observations are not private source witnesses.
        }
        return flags;
    }
    public async ValueTask RequireAsync(AgentRun run, CancellationToken ct, bool checkProfile = true)
    {
        if (run.IsTerminal)
        {
            var native = NativeSources(run);
            if ((native & 1) != 0) await (workspace ?? throw new AccessDeniedException("workspace_source_unavailable")).RequireStoredAsync(run, ct, live: false).ConfigureAwait(false);
            if ((native & 2) != 0) await (artifacts ?? throw new AccessDeniedException("artifact_source_unavailable")).RequireStoredAsync(run, ct, live: false).ConfigureAwait(false);
            if ((native & 4) != 0) await (browser ?? throw new AccessDeniedException("browser_source_unavailable")).RequireStoredObservationsAsync(run, ct, live: false).ConfigureAwait(false);
            if ((native & 8) != 0) await (browserEffects ?? throw new AccessDeniedException("browser_source_unavailable")).RequireStoredObservationAsync(run, ct, live: false).ConfigureAwait(false);
            if ((native & 16) != 0) await (sandboxReads ?? throw new AccessDeniedException("sandbox_source_unavailable")).RequireStoredAsync(run, ct).ConfigureAwait(false);
            if ((native & 32) != 0) await (sandboxEffects ?? throw new AccessDeniedException("sandbox_source_unavailable")).RequireStoredAsync(run, ct).ConfigureAwait(false);
        }
        if (run.Admission.ComposerInput is not null)
            await (composer ?? throw new AccessDeniedException("composer_source_unavailable")).RequireAsync(run, ct).ConfigureAwait(false);
        if (checkProfile && AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource is { } profile)
            await profiles.RequireAsync(run, profile, ct).ConfigureAwait(false);
        if (TenantMemoryOperationReferences.Read(run.Checkpoint) is { Count: > 0 } learned)
            await memories.RequireAsync(run, learned, ct).ConfigureAwait(false);
        if (TenantManagementReadReferences.Read(run.Checkpoint).Count > 0) await management.RequireAsync(run, ct).ConfigureAwait(false);
        if (TenantAutomationReadReferences.Read(run.Checkpoint).Count > 0) await automations.RequireAsync(run, ct).ConfigureAwait(false);
        if (TenantExperienceReferences.Sources(run.Checkpoint).Count > 0 || TenantExperienceReferences.Facts(run.Checkpoint).Count > 0)
            await continuity.RequireExperienceAsync(run, ct).ConfigureAwait(false);
        if (TenantHistoricalReferences.Read(run.Checkpoint) is { Count: > 0 } historical)
            await continuity.RequireAsync(run, historical, null, ct).ConfigureAwait(false);
    }
}
