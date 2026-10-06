using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public sealed record ToolApprovalPreparation(
    string ActionHash,
    string Preview,
    string ActionJson,
    IReadOnlyDictionary<string, string>? Details = null);

public static class ToolActionPreparation
{
    public static string ActionHash(SessionToolExecutor tools, ModelToolCall call, JsonElement args)
    {
        if (string.Equals(call.Name, ToolCatalog.HttpRequest, StringComparison.Ordinal))
        {
            var prepared = tools.PrepareHttpRequestApproval(args);
            if (prepared.Preparation is not null)
            {
                return prepared.Preparation.ActionHash;
            }
        }

        return ToolActionHash.Compute(call.Name, args);
    }

    public static async ValueTask<(ToolApprovalPreparation? Preparation, string? ErrorJson)> PrepareApprovalAsync(
        SessionToolExecutor tools,
        ModelToolCall call,
        JsonElement args,
        CancellationToken cancellationToken = default,
        AgentCore.Domain.Definitions.AgentDefinition? definition = null,
        Guid sessionId = default,
        ToolExecutionAdmission? admission = null)
    {
        if (call.Name is ToolCatalog.WorkspaceDelete or ToolCatalog.WorkspaceBatch)
        {
            try
            {
                var normalized = definition is not null && AgentCore.Domain.Definitions.WorkspaceSemantics.IsV2(definition)
                    ? AgentWorkspacePaths.Arguments(call.Name, args, sessionId, admission?.WorkspaceCwd ?? "/home") : args;
                var parsed = WorkspaceStructureArguments.Parse(call.Name, normalized);
                AgentCore.Application.Workspaces.WorkspaceStructuralPaths.Normalize(sessionId, parsed.Operations);
                var preview = ToolApprovalPreview.Build(call.Name, args);
                if (definition is not null && AgentCore.Domain.Definitions.WorkspaceSemantics.IsV2(definition))
                {
                    preview.Details["Exact operations"] = AgentWorkspacePaths.Project(normalized.GetRawText());
                    preview.Details["Path scope"] = "Resolved /home paths use this managed identity; /working paths use this Session scratch.";
                }
                return (new(ToolActionHash.Compute(call.Name, args), preview.Summary, call.ArgumentsJson, preview.Details), null);
            }
            catch (AgentCore.Application.Sessions.AgentCoreException)
            {
                return (null, """{"error":"invalid","message":"Filesystem approval requires valid bounded concrete paths and structural operations."}""");
            }
        }
        if (ToolCatalog.IsIdentityMaintenance(call.Name))
            return await tools.PrepareIdentityMaintenanceApprovalAsync(definition, sessionId, call, args, admission, cancellationToken);
        if (string.Equals(call.Name, ToolCatalog.HttpRequest, StringComparison.Ordinal))
        {
            var prepared = tools.PrepareHttpRequestApproval(args);
            if (prepared.Preparation is null)
            {
                return (null, prepared.ErrorJson);
            }

            return (
                new ToolApprovalPreparation(
                    prepared.Preparation.ActionHash,
                    prepared.Preparation.Summary,
                    call.ArgumentsJson,
                    prepared.Preparation.Details),
                null);
        }

        if (string.Equals(call.Name, ToolCatalog.EmailSend, StringComparison.Ordinal))
        {
            var prepared = await tools.PrepareEmailSendApprovalAsync(args, cancellationToken).ConfigureAwait(false);
            if (prepared.Preparation is null)
            {
                return (null, prepared.ErrorJson);
            }

            return (
                new ToolApprovalPreparation(
                    prepared.Preparation.ActionHash,
                    prepared.Preparation.Summary,
                    call.ArgumentsJson,
                    prepared.Preparation.Details),
                null);
        }

        var generic = ToolApprovalPreview.Build(call.Name, args);
        return (
            new ToolApprovalPreparation(
                ToolActionHash.Compute(call.Name, args),
                generic.Summary,
                call.ArgumentsJson,
                generic.Details),
            null);
    }
}
