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
        CancellationToken cancellationToken = default)
    {
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
