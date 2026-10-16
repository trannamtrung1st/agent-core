using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private static readonly JsonSerializerOptions StructureJsonOptions = new(JsonSerializerDefaults.Web);
    private async Task<string> StructureWorkspaceAsync(AgentDefinition definition, Guid sessionId, string tool, JsonElement args, CancellationToken ct)
    {
        if (tool == ToolCatalog.WorkspaceCopy
            && TryString(args, "source", out var crossSource) && TryString(args, "destination", out var crossDestination)
            && AgentHomePath.IsHome(crossSource) != AgentHomePath.IsHome(crossDestination))
        {
            if (args.EnumerateObject().Any(p => p.Name is not ("source" or "destination")))
                return Error("invalid", "Cross-scope copy accepts source and destination.");
            if (agentWorkspace is null) return Error("unavailable", "Agent Workspace is unavailable.");
            await agentWorkspace.CopyAcrossScopesAsync(sessionId, crossSource, crossDestination, ct);
            return JsonSerializer.Serialize(new { completed = true, source = crossSource, destination = crossDestination });
        }
        var request = WorkspaceStructureArguments.Parse(tool,args);
        var operations = WorkspaceStructuralPaths.Normalize(sessionId,request);
        var paths = operations.SelectMany(o => new[] {o.Path,o.Source,o.Destination}).OfType<string>().ToArray();
        var home = paths.All(AgentHomePath.IsHome);
        if (!home && paths.Any(AgentHomePath.IsHome)) throw AgentCoreErrors.Forbidden("Cross-scope move and batches are forbidden. Use individual workspace.copy, then explicit delete if needed.");
        WorkspaceStructureResult result;
        if (home)
        {
            if (agentWorkspace is null) return Error("unavailable","Durable home is unavailable for this execution.");
            result = await agentWorkspace.StructureAsync(sessionId,operations,ct);
        }
        else
        {
            if (workspace is null) return Error("unavailable","Workspace is unavailable.");
            await workspace.EnsureAsync(sessionId,definition,ct);
            result = await workspace.StructureAsync(sessionId,operations,ct);
        }
        return JsonSerializer.Serialize(new { result.Completed, result.OperationCount,result.CompletedCount,result.FailedIndex,result.Results,result.ErrorCode,result.Message,result.MutationsMayHaveOccurred, operations },StructureJsonOptions);
    }
}
