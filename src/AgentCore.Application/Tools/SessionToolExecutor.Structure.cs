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
            if (args.EnumerateObject().Any(p => p.Name is not ("source" or "destination" or "expectedRevision" or "expectedSha256")))
                return Error("invalid", "Cross-scope copy accepts source, destination and optional durable destination revision/hash.");
            if (agentWorkspace is null) return Error("unavailable", "Agent Workspace is unavailable.");
            await agentWorkspace.CopyAcrossScopesAsync(sessionId, crossSource, crossDestination, ExpectedRevision(args), ExpectedHash(args), ct);
            return JsonSerializer.Serialize(new { completed = true, source = crossSource, destination = crossDestination });
        }
        var request = WorkspaceStructureArguments.Parse(tool,args);
        var operations = WorkspaceStructuralPaths.Normalize(sessionId,request.Operations);
        var paths = operations.SelectMany(o => new[] {o.Path,o.Source,o.Destination}).OfType<string>().ToArray();
        var home = paths.All(AgentHomePath.IsHome);
        if (!home && paths.Any(AgentHomePath.IsHome)) throw AgentCoreErrors.Forbidden("Cross-scope move and batches are forbidden. Use individual workspace.copy, then explicit delete if needed.");
        WorkspaceStructureResult result;
        if (home)
        {
            if (agentWorkspace is null) return Error("unavailable","Durable home is unavailable for this execution.");
            if (request.TreeSha256 is null) throw AgentCoreErrors.Validation("Home restructuring requires expectedTreeSha256 from workspace.list.");
            result = await agentWorkspace.StructureAsync(sessionId,operations,request.TreeSha256,ct);
        }
        else
        {
            if (request.TreeSha256 is not null) throw AgentCoreErrors.Validation("expectedTreeSha256 applies only to durable /home.");
            if (workspace is null) return Error("unavailable","Workspace is unavailable.");
            await workspace.EnsureAsync(sessionId,definition,ct);
            result = await workspace.StructureAsync(sessionId,operations,ct);
        }
        return JsonSerializer.Serialize(new { result.Completed, result.OperationCount,result.CompletedCount,result.FailedIndex,result.Results,result.ErrorCode,result.Message,result.MutationsMayHaveOccurred,result.TreeSha256, operations },StructureJsonOptions);
    }
}
