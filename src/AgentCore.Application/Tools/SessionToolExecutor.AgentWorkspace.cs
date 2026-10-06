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

    private async Task<string> RetainWorkspaceAsync(Guid sessionId, JsonElement args, CancellationToken ct)
    {
        if (agentWorkspace is null) return Error("unavailable", "Durable home is unavailable for this execution.");
        if (args.EnumerateObject().Any(p => p.Name is not ("source" or "destination" or "expectedRevision" or "expectedSha256")))
            return Error("invalid", "Only source, destination and expected revision/hash are accepted.");
        if (!TryString(args, "source", out var source) || !TryString(args, "destination", out var destination))
            return Error("invalid", "source and destination are required.");
        return JsonSerializer.Serialize(await agentWorkspace.RetainAsync(sessionId, source, destination,
            ExpectedRevision(args), ExpectedHash(args), ct), JsonOptions);
    }

    private async Task<string> CheckoutWorkspaceAsync(Guid sessionId, JsonElement args, CancellationToken ct)
    {
        if (agentWorkspace is null) return Error("unavailable", "Durable home is unavailable for this execution.");
        if (args.EnumerateObject().Any(p => p.Name is not ("source" or "destination" or "expectedRevision" or "expectedSha256")))
            return Error("invalid", "Only source, destination and expected revision/hash are accepted.");
        if (!TryString(args, "source", out var source)) return Error("invalid", "source is required.");
        TryString(args, "destination", out var destination);
        return JsonSerializer.Serialize(await agentWorkspace.CheckoutAsync(sessionId, source, string.IsNullOrEmpty(destination) ? null : destination,
            ExpectedRevision(args), ExpectedHash(args), ct), JsonOptions);
    }

    private static long? ExpectedRevision(JsonElement args)
    {
        if (!args.TryGetProperty("expectedRevision", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var revision) || revision < 1)
            throw AgentCoreErrors.Validation("expectedRevision must be a positive integer.");
        return revision;
    }
    private static string? ExpectedHash(JsonElement args)
    {
        if (!args.TryGetProperty("expectedSha256", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw AgentCoreErrors.Validation("expectedSha256 must be a SHA-256 string.");
        return value.GetString();
    }

    private async ValueTask<WorkspaceContent> ReadExecutionWorkspaceAsync(Guid sessionId, AgentDefinition definition, string path, CancellationToken ct)
    {
        if (!AgentHomePath.IsHome(path)) return await workspace!.ReadAsync(sessionId, definition, path, ct);
        if (agentWorkspace is null) throw AgentCoreErrors.Validation("Durable home is unavailable for this execution.");
        var content = await agentWorkspace.ReadAsync(await agentWorkspace.SessionOwnerAsync(sessionId, ct), path: path, cancellationToken: ct);
        return new(path, content.Item.ContentType, content.Bytes);
    }

    private async ValueTask<IReadOnlyList<WorkspaceNode>> ListExecutionWorkspaceAsync(Guid sessionId, AgentDefinition definition, string path, CancellationToken ct)
    {
        if (AgentHomePath.IsHome(path))
        {
            if (agentWorkspace is null) throw AgentCoreErrors.Validation("Durable home is unavailable for this execution.");
            return await agentWorkspace.ListNodesAsync(await agentWorkspace.SessionOwnerAsync(sessionId, ct), AgentHomePath.Normalize(path, false), ct);
        }
        var nodes = await workspace!.ListAsync(sessionId, definition, path, ct);
        if (path != "/" || agentWorkspace is null) return nodes;
        try { await agentWorkspace.SessionOwnerAsync(sessionId, ct); }
        catch (AgentCoreException ex) when (ex.Code == "ValidationError") { return nodes; }
        return [..nodes, new("/home", true, 0, false)];
    }
}
