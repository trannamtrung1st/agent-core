using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private async Task<ToolExecutionResult> ExecuteCwdAsync(AgentDefinition definition, Guid sessionId, JsonElement args, string cwd, CancellationToken ct)
    {
        if (args.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != args.EnumerateObject().Count()
            || args.EnumerateObject().Any(p => p.Name is not ("operation" or "path"))
            || !TryString(args, "operation", out var operation) || operation is not ("get" or "set"))
            return TextResult(Error("invalid", "Use operation get, or set with an existing directory path."));
        if (operation == "get")
            return args.TryGetProperty("path", out _) ? TextResult(Error("invalid", "get does not accept path.")) : TextResult(JsonSerializer.Serialize(new { cwd }));
        if (!TryString(args, "path", out var path)) return TextResult(Error("invalid", "set requires path."));
        path = AgentWorkspacePaths.Resolve(path, sessionId, cwd);
        if (!AgentHomePath.IsHome(path) && path != WorkspaceLogicalPath.WorkingDirectory
            && !path.StartsWith(WorkspaceLogicalPath.WorkingDirectory + "/", StringComparison.Ordinal))
            return TextResult(Error("forbidden", "cwd must be an authorized /home or /working directory."));
        await workspace!.EnsureAsync(sessionId, definition, ct);
        if (path is not "/home" && path != WorkspaceLogicalPath.WorkingDirectory)
        {
            var parent = path[..path.LastIndexOf('/')];
            var node = (await ListExecutionWorkspaceAsync(sessionId, definition, parent, ct)).SingleOrDefault(n => n.LogicalPath == path);
            if (node is null) return TextResult(Error("notFound", "cwd target directory does not exist."));
            if (!node.Directory) return TextResult(Error("invalid", "cwd target must be a directory."));
        }
        var next = AgentWorkspacePaths.Public(path);
        return new(JsonSerializer.Serialize(new { cwd = next }), WorkspaceCwd: next);
    }

    private static void ProtectCwd(string tool, JsonElement args, Guid sessionId, string cwd)
    {
        if (tool is not (ToolCatalog.WorkspaceMove or ToolCatalog.WorkspaceDelete or ToolCatalog.WorkspaceBatch)) return;
        var operations = WorkspaceStructureArguments.Parse(tool, args).Operations;
        var current = AgentWorkspacePaths.Resolve(cwd, sessionId);
        foreach (var operation in operations)
        {
            var source = operation.Op == "move" ? operation.Source : operation.Op == "delete" ? operation.Path : null;
            if (source is not null && (current == source || current.StartsWith(source + "/", StringComparison.Ordinal)))
                throw AgentCoreErrors.Conflict("Current working directory or its ancestor cannot be moved or deleted. Set cwd to another directory first.");
        }
    }
}
