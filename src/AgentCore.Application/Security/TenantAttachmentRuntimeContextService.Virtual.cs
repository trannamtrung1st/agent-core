using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public sealed record TenantAttachmentVirtualRead(string Tool, string ArgumentsJson);

public sealed partial class TenantAttachmentRuntimeContextService
{
    private async ValueTask RequireVirtual(TenantRuntimeFrame frame, string tool, string arguments, CancellationToken ct, bool approved = true)
    {
        var args = TenantAttachmentVirtualReadContract.Resolve(tool, arguments);
        var (view, source) = await RequireRead(frame, args, ct, approved: approved).ConfigureAwait(false);
        var current = await configurations.CurrentPolicyAsync(frame.Owner.TenantId!.Value, frame.Owner.AgentInstanceId, ct).ConfigureAwait(false);
        if (!TenantWorkspaceReadContract.Allows(view.Run.Admission.Configuration!.Definition, tool)
            || !TenantWorkspaceReadContract.Allows(current.Definition, tool)) throw new AccessDeniedException("capability_unavailable");
        if (!TenantAttachmentVirtualReadContract.AcceptsContentType(tool, source.ContentType)) throw new AccessDeniedException("unsupported_reader");
        await MetadataFence(frame, view, source, args, ct).ConfigureAwait(false);
    }

    public async ValueTask<string> VirtualArgumentsAsync(TenantRuntimeFrame frame, ModelToolCall call, CancellationToken ct = default)
    {
        await RequireVirtual(frame, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
        return TenantAttachmentReadContract.Canonical(TenantAttachmentVirtualReadContract.Resolve(call.Name, call.ArgumentsJson));
    }

    public async ValueTask<string> InspectVirtualAsync(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct = default)
    {
        try { return await InspectVirtualCore(frame, call, remainingBytes, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
        {
            if (frame.Owner.TenantId is { } tenant)
                await operations.RecordFailureAsync(new(tenant, frame.RunId), "attachments.read",
                    error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode,
                    AgentCore.Domain.Security.ExecutionAuthorizationPurpose.ToolBoundary, ct, frame.SessionId).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<string> InspectVirtualCore(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct)
    {
        // Policy refusal precedes even reader/media compatibility, so it cannot become a metadata probe.
        await RequireVirtual(frame, call.Name, call.ArgumentsJson, ct, approved: false).ConfigureAwait(false);
        var arguments = TenantAttachmentReadContract.Canonical(TenantAttachmentVirtualReadContract.Resolve(call.Name, call.ArgumentsJson));
        var args = TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson);
        var origin = new TenantAttachmentVirtualRead(call.Name, TenantWorkspaceReadContract.Canonical(call.Name, args));
        // Reserve the real JSON overhead before clipping content or complete Office elements.
        // Fitting a full canonical response first would discard its useful prefix after wrapping.
        var overhead = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { path = args.Path, attachmentVirtualRead = origin })) - 1;
        // One source, parser, approval fingerprint and refusal history, regardless of virtual spelling.
        var result = await InspectReadCore(frame, arguments, ReadVision((await Load(frame, ct).ConfigureAwait(false)).Run), Math.Max(0, remainingBytes - overhead), officeReader, ct, approved: false).ConfigureAwait(false);
        await RequireVirtual(frame, call.Name, call.ArgumentsJson, ct, approved: false).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Text)) return result.Text;
        var payload = JsonNode.Parse(result.Text)?.AsObject();
        if (payload is null || payload["tenantAttachmentRead"]?.GetValue<bool>() != true) return result.Text;
        payload["path"] = args.Path;
        payload["attachmentVirtualRead"] = JsonSerializer.SerializeToNode(origin);
        return ToolJsonResults.FitToBudget(remainingBytes, payload.ToJsonString());
    }

    private static TenantAttachmentVirtualRead? VirtualObservation(ModelMessage message, TenantAttachmentReadObservation observation)
    {
        if (message.Name == ToolCatalog.AttachmentsRead) return null;
        try
        {
            using var doc = JsonDocument.Parse(message.Text);
            var saved = doc.RootElement.GetProperty("attachmentVirtualRead").Deserialize<TenantAttachmentVirtualRead>() ?? throw new AccessDeniedException("attachment_source_changed");
            var args = TenantWorkspaceReadContract.Parse(saved.Tool, saved.ArgumentsJson);
            if (saved.Tool != message.Name || TenantWorkspaceReadContract.Canonical(saved.Tool, args) != saved.ArgumentsJson
                || doc.RootElement.GetProperty("path").GetString() != args.Path
                || TenantAttachmentReadContract.Canonical(TenantAttachmentVirtualReadContract.Resolve(saved.Tool, saved.ArgumentsJson)) != observation.ArgumentsJson)
                throw new AccessDeniedException("attachment_source_changed");
            return saved;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new AccessDeniedException("attachment_source_changed"); }
    }
}
