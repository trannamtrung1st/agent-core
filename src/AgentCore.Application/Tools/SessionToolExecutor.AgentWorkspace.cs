using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    public async ValueTask<bool> AgentWorkspaceAvailableAsync(Guid sessionId, CancellationToken ct)
    {
        if (agentWorkspace is null) return false;
        try { await agentWorkspace.SessionOwnerAsync(sessionId, ct); return true; }
        catch (AgentCoreException ex) when (ex.Code is "ValidationError" or "NotFound") { return false; }
    }

    private async ValueTask<WorkspaceContent> ReadExecutionWorkspaceAsync(Guid sessionId, AgentDefinition definition, string path, CancellationToken ct)
    {
        AgentCore.Application.Agents.StateIsolationAccess.RequireHome(await IsolationAsync(definition, sessionId, ct), path, false);
        if (!AgentHomePath.IsHome(path)) return await workspace!.ReadAsync(sessionId, definition, path, ct);
        if (agentWorkspace is null) throw AgentCoreErrors.Validation("Durable home is unavailable for this execution.");
        var content = await agentWorkspace.ReadAsync(await agentWorkspace.SessionOwnerAsync(sessionId, ct), path: path, cancellationToken: ct);
        return new(path, content.Item.ContentType, content.Bytes);
    }

    private async ValueTask<WorkspaceNodePage> ListExecutionWorkspacePageAsync(Guid sessionId, AgentDefinition definition,
        string path, string? cursor, CancellationToken ct)
    {
        var isolation = await IsolationAsync(definition, sessionId, ct);
        AgentCore.Application.Agents.StateIsolationAccess.RequireHome(isolation, path, false);
        if (AgentHomePath.IsHome(path))
        {
            if (agentWorkspace is null) throw AgentCoreErrors.Validation("Durable home is unavailable for this execution.");
            return await agentWorkspace.ListNodePageAsync(await agentWorkspace.SessionOwnerAsync(sessionId, ct),
                AgentHomePath.Normalize(path, false), cursor, AgentCore.Domain.Conversation.WorkspaceLimits.MaxListEntries, ct);
        }
        var page = await workspace!.ListPageAsync(sessionId, definition, path, cursor, AgentCore.Domain.Conversation.WorkspaceLimits.MaxListEntries, ct);
        if (path != "/" || agentWorkspace is null || isolation.HomeAccess == HomeAccess.Disabled) return page;
        try { await agentWorkspace.SessionOwnerAsync(sessionId, ct); }
        catch (AgentCoreException ex) when (ex.Code == "ValidationError") { return page; }
        return page with { Entries = [..page.Entries, new("/home", true, 0, false)] };
    }
}
