using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Resolve factual provenance only from the owned root input or actual paired checkpoint observations.
public static class TenantManagementResourceSources
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static TenantManagementResourceSource? Material(TenantManagementReader reader, TenantManagementMutationArguments args,
        IReadOnlyList<ConversationEntry> entries, Func<Guid, TenantApprovalRequest?> requests)
    {
        try { return MaterialCore(reader, args, entries, requests); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new AccessDeniedException("resource_source_unavailable"); }
    }
    private static TenantManagementResourceSource? MaterialCore(TenantManagementReader reader, TenantManagementMutationArguments args,
        IReadOnlyList<ConversationEntry> entries, Func<Guid, TenantApprovalRequest?> requests)
    {
        if (args.Tool != "resources.upsert") return null;
        var name = args.Value.GetProperty("source").GetString()!;
        if (name == "conversation:user")
        {
            var root = reader.Root; var ids = root.Admission.Activation.SourceEntryIds;
            if (root.Admission.ExecutionContext is not { Origin: ExecutionOrigin.UserMessage, OriginalInitiatorPrincipalId: not null }
                || ids.Count == 0 || entries.Count != ids.Count || entries.Select(e => e.EntryId).Distinct().Count() != ids.Count
                || entries.Any(e => !ids.Contains(e.EntryId) || e.Role != ConversationRole.User)
                || !entries.Any(e => !string.IsNullOrWhiteSpace(e.Text)) || entries.Sum(e => Encoding.UTF8.GetByteCount(e.Text)) > 65536)
                throw new AccessDeniedException("resource_source_unavailable");
            return new(name, root.AgentRunId, TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(entries.OrderBy(e => e.Sequence)
                .Select(e => new { e.EntryId, e.Sequence, e.Role, e.Text }))));
        }
        if (!AgentRunToolCallCheckpoint.TryRead(reader.Run.Checkpoint, out var messages)) throw new AccessDeniedException("resource_source_unavailable");
        IReadOnlyList<ModelToolCall> batch = []; TenantManagementResourceSource? selected = null;
        foreach (var message in messages!)
        {
            if (message.Role == ModelRole.Assistant) batch = message.ToolCalls ?? [];
            if (message.Role != ModelRole.Tool) continue;
            var call = batch.SingleOrDefault(c => c.Id == message.ToolCallId && c.Name == message.Name);
            if (call is null) continue;
            using var doc = Parse(message.Text); if (doc is null) continue; var envelope = doc.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object || envelope.TryGetProperty("error", out _)) continue;
            var data = envelope; Guid? requestId = null; Guid? operationId = null;
            if (call.Name == ToolCatalog.RequestAuthorization)
            {
                if (envelope.TryGetProperty("workspaceObservation", out var workspace) && workspace.ValueKind == JsonValueKind.Object) data = workspace;
                else if (envelope.TryGetProperty("attachmentObservation", out var attachment) && attachment.ValueKind == JsonValueKind.Object) data = attachment;
                else if (envelope.TryGetProperty("browser", out var browser) && browser.ValueKind == JsonValueKind.Object) data = browser;
                else continue;
                if (!envelope.TryGetProperty("requestId", out var request) || !request.TryGetGuid(out var requested)
                    || !envelope.TryGetProperty("operationId", out var operation) || !operation.TryGetGuid(out var operated)
                    || envelope.TryGetProperty("outcome", out var outcome) && outcome.GetString() != "completed") continue;
                requestId = requested; operationId = operated;
            }
            if (!HasMaterial(data)) continue;
            var hash = TenantMemoryOperationRules.Hash(message.Text);
            if (data.TryGetProperty("tenantWorkspaceRead", out var marker) && marker.ValueKind == JsonValueKind.True)
            {
                var witness = data.GetProperty("workspaceRead"); var tool = witness.GetProperty("tool").GetString()!;
                var parsed = witness.GetProperty("arguments").Deserialize<TenantWorkspaceReadArguments>(Json) ?? throw new AccessDeniedException("resource_source_unavailable");
                var canonical = TenantWorkspaceReadContract.Canonical(tool, parsed); parsed = TenantWorkspaceReadContract.Parse(tool, canonical);
                if (!TenantWorkspaceReadContract.IsFile(tool) || (tool == ToolCatalog.KnowledgeRetrieve ? "knowledge:" + parsed.Identity : "workspace:" + parsed.Path) != name) continue;
                if (call.Name != ToolCatalog.RequestAuthorization && (call.Name != tool
                    || TenantWorkspaceReadContract.Canonical(tool, TenantWorkspaceReadContract.Parse(tool, call.ArgumentsJson)) != canonical))
                    throw new AccessDeniedException("resource_source_unavailable");
                PreparedWorkspaceRead read;
                if (requestId is { } id)
                {
                    read = Request(reader, requests(id), id, operationId!.Value, call).Operation.WorkspaceRead ?? throw new AccessDeniedException("resource_source_unavailable");
                    if (read.ToolName != tool || read.ArgumentsJson != canonical) throw new AccessDeniedException("resource_source_unavailable");
                }
                else
                {
                    var resource = witness.TryGetProperty("resource", out var binding) ? binding.Deserialize<TenantResourceSourceObservation>(Json) : null;
                    var known = tool == ToolCatalog.KnowledgeRetrieve ? TenantKnowledgeReadContract.Resolve(reader.Run.Admission.Configuration!.Definition,
                        reader.Run.Admission.Configuration.Resources, parsed.Identity!).Source : null;
                    read = new(reader.Run.SessionId, parsed.Path.StartsWith("/working/", StringComparison.Ordinal), tool, canonical,
                        witness.TryGetProperty("observationHash", out var observed) ? observed.GetString()! : hash,
                        resource?.Resource.Key, resource?.ResourceRevision, Knowledge: known);
                }
                selected = new(name, reader.Root.AgentRunId, hash, call.Id, tool, canonical, requestId, operationId, Workspace: read);
            }
            else if (data.TryGetProperty("tenantAttachmentRead", out marker) && marker.ValueKind == JsonValueKind.True)
            {
                var observation = data.GetProperty("attachmentRead").Deserialize<TenantAttachmentReadObservation>(Json) ?? throw new AccessDeniedException("resource_source_unavailable");
                var canonical = TenantAttachmentReadContract.Canonical(TenantAttachmentReadContract.Parse(observation.ArgumentsJson));
                TenantAttachmentVirtualRead? virtualRead = data.TryGetProperty("attachmentVirtualRead", out var origin) ? origin.Deserialize<TenantAttachmentVirtualRead>(Json) : null;
                if (call.Name == ToolCatalog.RequestAuthorization && TenantApprovalToolContract.Parse(call).WorkspaceRead is { } virtualIntent)
                    virtualRead = new(virtualIntent.Tool, virtualIntent.ArgumentsJson);
                string? virtualPath = null;
                if (virtualRead is not null)
                {
                    virtualPath = TenantWorkspaceReadContract.Parse(virtualRead.Tool, virtualRead.ArgumentsJson).Path;
                    if (!TenantAttachmentVirtualReadContract.IsTool(virtualRead.Tool) || !TenantAttachmentVirtualReadContract.IsPath(virtualPath)
                        || !TenantAttachmentVirtualReadContract.AcceptsContentType(virtualRead.Tool, observation.Source.ContentType)
                        || TenantAttachmentReadContract.Canonical(TenantAttachmentVirtualReadContract.Resolve(virtualRead.Tool, virtualRead.ArgumentsJson)) != canonical)
                        throw new AccessDeniedException("resource_source_unavailable");
                }
                if ("attachment:" + observation.Source.AttachmentId.ToString("D") != name && (virtualPath is null || "workspace:" + virtualPath != name)) continue;
                if (call.Name != ToolCatalog.RequestAuthorization)
                {
                    if (virtualRead is null ? call.Name != ToolCatalog.AttachmentsRead || TenantAttachmentReadContract.Canonical(TenantAttachmentReadContract.Parse(call.ArgumentsJson)) != canonical
                        : call.Name != virtualRead.Tool || TenantWorkspaceReadContract.Canonical(call.Name, TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson)) != virtualRead.ArgumentsJson)
                        throw new AccessDeniedException("resource_source_unavailable");
                }
                var source = observation.Source;
                var read = requestId is { } id ? Request(reader, requests(id), id, operationId!.Value, call).Operation.AttachmentRead
                    ?? throw new AccessDeniedException("resource_source_unavailable")
                    : new PreparedAttachmentRead(reader.Run.SessionId, source.AttachmentId, source.EntryId, source.DisplayName, source.ContentType,
                        source.ByteSize, source.Sha256Hex, source.ProcessorVersion, canonical,
                        data.TryGetProperty("inspection", out var inspection) && inspection.TryGetProperty("kind", out var kind) && kind.GetString() == "image");
                if (read.ArgumentsJson != canonical || read.AttachmentId != source.AttachmentId || read.EntryId != source.EntryId
                    || read.DisplayName != source.DisplayName || read.ContentType != source.ContentType || read.ByteSize != source.ByteSize
                    || read.Sha256Hex != source.Sha256Hex || read.ProcessorVersion != source.ProcessorVersion)
                    throw new AccessDeniedException("resource_source_unavailable");
                selected = new(name, reader.Root.AgentRunId, hash, call.Id, virtualRead?.Tool ?? ToolCatalog.AttachmentsRead, virtualRead?.ArgumentsJson ?? canonical, requestId, operationId, Attachment: read);
            }
            else if (data.TryGetProperty("untrustedBrowserContent", out marker) && marker.ValueKind == JsonValueKind.True && BrowserUrl(data) == name)
            {
                if (requestId is { } id)
                {
                    var command = Request(reader, requests(id), id, operationId!.Value, call).Operation.BrowserCommand ?? throw new AccessDeniedException("resource_source_unavailable");
                    selected = new(name, reader.Root.AgentRunId, hash, call.Id, command.ToolName, command.ArgumentsJson, requestId, operationId);
                }
                else if (TenantBrowserInspectionContract.IsInspection(call.Name) && data.TryGetProperty("tenantBrowserRead", out var supplied) && supplied.ValueKind == JsonValueKind.True)
                {
                    var normalized = TenantBrowserInspectionContract.Normalize(call);
                    var command = TenantBrowserInspectionContract.Parse(reader.Run.SessionId, normalized);
                    if (data.GetProperty("readCommandHash").GetString() != BrowserCommandIdentity.Hash(command)) throw new AccessDeniedException("resource_source_unavailable");
                    selected = new(name, reader.Root.AgentRunId, hash, call.Id, normalized.Name, normalized.ArgumentsJson);
                }
            }
        }
        return selected ?? throw new AccessDeniedException("resource_source_unavailable");
    }

    private static TenantApprovalRequest Request(TenantManagementReader reader, TenantApprovalRequest? request, Guid id, Guid operation, ModelToolCall? call = null) =>
        request is { Status: TenantApprovalStatus.Approved } && request.RequestId == id && request.Operation.OperationId == operation
        && request.TenantId == reader.Security.Tenant.TenantId && request.SessionId == reader.Run.SessionId
        && request.ExecutorPrincipalId == reader.Agent.PrincipalId && request.Operation.InstanceId == reader.Run.AgentInstanceId
        && request.RootActivationId == reader.Root.Admission.Activation.RootActivationId
        && request.OriginalInitiatorPrincipalId == reader.Root.Admission.ExecutionContext!.OriginalInitiatorPrincipalId
        && (call is null || TenantApprovalToolContract.Parse(call).Matches(request.Operation)) ? request : throw new AccessDeniedException("resource_source_unavailable");

    private static string? BrowserUrl(JsonElement data)
    {
        if (data.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String) return url.GetString();
        foreach (var field in new[] { "observation", "data" })
            if (data.TryGetProperty(field, out var nested) && nested.ValueKind == JsonValueKind.Object && BrowserUrl(nested) is { } found) return found;
        return null;
    }
    private static JsonDocument? Parse(string text) { try { return JsonDocument.Parse(text); } catch (JsonException) { return null; } }
    private static bool HasMaterial(JsonElement value, int depth = 0)
    {
        if (depth > 12) return false;
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(item => HasMaterial(item, depth + 1));
        if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty("error", out _)
            || value.TryGetProperty("observationUnavailable", out var unavailable) && unavailable.ValueKind == JsonValueKind.True
            || value.TryGetProperty("contentProvided", out var absent) && absent.ValueKind == JsonValueKind.False) return false;
        foreach (var field in new[] { "content", "contentExcerpt", "text", "visibleText", "body", "structure", "snapshot", "displayValue", "value", "formula" })
            if (value.TryGetProperty(field, out var text) && (text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
                || field == "value" && text.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)) return true;
        if (value.TryGetProperty("contentProvided", out var supplied) && supplied.ValueKind == JsonValueKind.True) return true;
        foreach (var field in new[] { "inspection", "observation", "data", "paragraphs", "cells", "sheets", "rows", "elements", "blocks" })
        {
            if (!value.TryGetProperty(field, out var nested)) continue;
            if (nested.ValueKind == JsonValueKind.Object && HasMaterial(nested, depth + 1)
                || nested.ValueKind == JsonValueKind.Array && nested.EnumerateArray().Any(item => HasMaterial(item, depth + 1))) return true;
        }
        return false;
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(TenantManagementReader reader, TenantManagementResourceSource? source)
    {
        if (source is null || source.Name == "conversation:user") return [];
        var tenant = reader.Security.Tenant.TenantId;
        return source.Workspace is { } workspace ? TenantWorkspaceReadOperationRules.Targets(tenant, reader.Run.AgentInstanceId, workspace)
            : source.Attachment is { } attachment ? TenantAttachmentReadRules.Targets(tenant, attachment)
            : TenantBrowserOperationRules.Targets(tenant, reader.Run.AgentInstanceId, reader.Run.SessionId, true);
    }
    public static async ValueTask RawAsync(TenantManagementReader reader, TenantManagementResourceSource? source, Guid actor, IamAuthorizer iam, CancellationToken ct, bool requireCurrentOwnership = false)
    {
        foreach (var (permission, scope) in Targets(reader, source))
            if (!(await (requireCurrentOwnership ? iam.CheckTenantAsync(reader.Security, actor, PermissionCatalog.Get(permission), scope, ct)
                : iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get(permission), scope, ct)).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("resource_source_read_required");
    }
    public static void RequireVirtualSource(TenantManagementReader reader, TenantManagementResourceSource source)
    {
        if (source.Attachment is not { } attachment || source.ToolName == ToolCatalog.AttachmentsRead) return;
        if (!TenantAttachmentVirtualReadContract.IsTool(source.ToolName!)
            || !TenantWorkspaceReadContract.Allows(reader.Run.Admission.Configuration!.Definition, source.ToolName!)
            || !TenantWorkspaceReadContract.Allows(reader.Current, source.ToolName!)
            || !TenantAttachmentVirtualReadContract.AcceptsContentType(source.ToolName!, attachment.ContentType)
            || TenantAttachmentReadContract.Canonical(TenantAttachmentVirtualReadContract.Resolve(source.ToolName!, source.ArgumentsJson!)) != attachment.ArgumentsJson)
            throw new AccessDeniedException("resource_source_changed");
    }
    public static async ValueTask ConsentAsync(TenantManagementReader reader, TenantManagementResourceSource? source,
        TenantApprovalRequest? request, TenantApprovalDecision? decision, TenantOperationReceipt? receipt, TenantOperationGrant? grant,
        IamAuthorizer iam, CancellationToken ct)
    {
        if (source is null || source.Name == "conversation:user") return;
        if (source.ReadRequestId is not { } id)
        {
            foreach (var (permission, scope) in Targets(reader, source))
                if (await TenantManagementReadRules.NeedsReviewOwnedAsync(reader, permission, scope, iam, ct).ConfigureAwait(false))
                    throw new AccessDeniedException("resource_source_read_review_required");
            return;
        }
        request = Request(reader, request, id, source.ReadOperationId ?? Guid.Empty);
        if (decision is not { Status: TenantApprovalStatus.Approved } || decision.RequestId != request.RequestId
            || decision.RequestRevision != request.Revision || decision.ActionHash != request.Operation.ActionHash
            || receipt is not { Outcome: TenantOperationOutcome.Completed } || receipt.RequestId != id || receipt.OperationId != request.Operation.OperationId
            || receipt.TenantId != request.TenantId || receipt.ActionHash != request.Operation.ActionHash || receipt.RootActivationId != request.RootActivationId
            || receipt.InstanceId != request.Operation.InstanceId || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.SessionId != request.SessionId)
            throw new AccessDeniedException("resource_source_unavailable");
        if (source.Workspace is { } workspace)
        {
            if (request.Kind != ApprovalKind.Review || grant is not null || request.Operation.WorkspaceRead != workspace || receipt.ReasonCode != TenantWorkspaceReadOperationRules.Reason)
                throw new AccessDeniedException("resource_source_unavailable");
            await TenantWorkspaceReadOperationRules.ReviewerAsync(reader.Security, reader.Run.AgentInstanceId, workspace, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
        }
        else if (source.Attachment is { } attachment)
        {
            if (request.Kind != ApprovalKind.Review || grant is not null || request.Operation.AttachmentRead != attachment || receipt.ReasonCode != TenantAttachmentReadRules.Reason)
                throw new AccessDeniedException("resource_source_unavailable");
            await TenantAttachmentReadRules.ReviewerAsync(reader.Security, attachment, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
        }
        else if (request.Operation.BrowserCommand is { } browser)
        {
            if (browser.ToolName != source.ToolName || browser.ArgumentsJson != source.ArgumentsJson || receipt.ReasonCode != TenantBrowserEffectRules.Completed
                || request.Kind == ApprovalKind.Review && grant is not null
                || request.Kind == ApprovalKind.ExceptionalGrant && (grant is not { Status: OperationGrantStatus.Consumed } || grant.RequestId != request.RequestId
                    || grant.TenantId != request.TenantId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId
                    || grant.ActionHash != request.Operation.ActionHash || grant.OperationId != request.Operation.OperationId))
                throw new AccessDeniedException("resource_source_unavailable");
            await TenantBrowserOperationRules.RequireReviewerAsync(reader.Security, reader.Run.AgentInstanceId, browser, request.OriginalInitiatorPrincipalId,
                decision.HumanPrincipalId, iam, ct, request.Kind).ConfigureAwait(false);
        }
        else throw new AccessDeniedException("resource_source_unavailable");
    }
}
