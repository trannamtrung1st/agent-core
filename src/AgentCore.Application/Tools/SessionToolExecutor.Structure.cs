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
        if (tool == ToolCatalog.WorkspaceMove && !WorkspaceFilesystemPolicy.AllowsStructure(definition))
            return await MoveLegacyWorkspaceFileAsync(sessionId, args, ct);
        var request = WorkspaceStructureArguments.Parse(tool,args);
        var operations = WorkspaceStructuralPaths.Normalize(sessionId,request.Operations);
        var paths = operations.SelectMany(o => new[] {o.Path,o.Source,o.Destination}).OfType<string>().ToArray();
        var home = paths.All(AgentHomePath.IsHome);
        if (!home && paths.Any(AgentHomePath.IsHome)) throw AgentCoreErrors.Forbidden("A structural operation cannot cross workspace scopes. Use retain or checkout.");
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
    private async Task<string> MoveLegacyWorkspaceFileAsync(Guid sessionId, JsonElement args, CancellationToken ct)
    {
        if (workspace is null) return Error("unavailable", "Workspace is unavailable.");
        if (!TryString(args, "source", out var source) || !TryString(args, "destination", out var destination))
            return Error("invalid", "source and destination are required.");
        if (!TryResolveWorkspacePath(source, sessionId, out source, out var sourceError)) return sourceError;
        if (!TryResolveWorkspacePath(destination, sessionId, out destination, out var destinationError)) return destinationError;
        if (AgentHomePath.IsHome(source) || AgentHomePath.IsHome(destination))
            throw AgentCoreErrors.Forbidden("This definition authorizes scratch file moves only. Structural workspace tools require explicit opt-in.");
        await workspace.MoveAsync(sessionId, source, destination, ct);
        return JsonSerializer.Serialize(new { source, destination });
    }
}
