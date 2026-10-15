using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    internal async ValueTask<AgentDefinition> RestrictExecutionDefinitionAsync(AgentDefinition admitted, Guid owner, CancellationToken ct)
    {
        if (_agentInstances is null || _agentDefinitions is null) return admitted;
        var instance = await _agentInstances.FindAsync(owner, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        var baseline = await _agentDefinitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct)
            ?? throw AgentCoreErrors.Persistence("Current Definition is unavailable.");
        return StateIsolationAccess.RestrictDefinition(admitted, InstanceSettingsResolver.Resolve(baseline, instance.SettingsOverrides));
    }
    internal ValueTask<bool> CleanupTerminalWorkingAsync(Guid sessionId, CancellationToken ct) =>
        workingCleanup?.CleanupAsync(sessionId, ct) ?? ValueTask.FromResult(false);
    internal async ValueTask<bool> FinalizeRequestResourcesAsync(Guid sessionId, CancellationToken ct)
    {
        var lease = sandboxStore is null ? null : await sandboxStore.GetLeaseAsync(sessionId, ct);
        if (lease is null || lease.State == AgentCore.Domain.Conversation.SandboxLeaseState.Terminated) return true;
        if (lease.ActiveExecutionId is not null || lease.State != AgentCore.Domain.Conversation.SandboxLeaseState.Ready || sandboxService is null) return false;
        await sandboxService.StopSessionAsync(sessionId, ct, lease.Generation, lease.LastExecutionId, true);
        return (await sandboxStore!.GetLeaseAsync(sessionId, ct))?.State == AgentCore.Domain.Conversation.SandboxLeaseState.Terminated;
    }
    private async ValueTask<StateIsolationPolicy> IsolationAsync(AgentDefinition definition, Guid sessionId, CancellationToken ct)
    {
        var session = _profiles is null ? null : await _profiles.LoadMetadataAsync(sessionId, ct);
        return await StateIsolationAccess.ResolveAsync(definition, session?.AgentInstanceId ?? Guid.Empty, _agentInstances, _agentDefinitions, ct);
    }

    private static void CheckWorkspacePolicy(string tool, JsonElement args, StateIsolationPolicy policy)
    {
        if (tool == ToolCatalog.WorkspaceBatch && args.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
        {
            foreach (var operation in operations.EnumerateArray())
                Check(operation, operation.TryGetProperty("op", out var op) ? op.GetString() : null);
        }
        else Check(args, tool == ToolCatalog.WorkspaceCopy ? "copy" : null);

        void Check(JsonElement item, string? operation)
        {
            var readOnly = tool is ToolCatalog.WorkspaceRead or ToolCatalog.WorkspaceList or ToolCatalog.WorkspaceSearch
                or ToolCatalog.WorkspaceCwd or ToolCatalog.ArtifactsCreateFromWorkspace;
            foreach (var field in new[] { "path", "source", "destination" })
                if (item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                    StateIsolationAccess.RequireHome(policy, value.GetString()!, !readOnly && !(operation == "copy" && field == "source"));
        }
    }
}
