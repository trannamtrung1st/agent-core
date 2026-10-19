using System.Text;
using System.Text.Json;
using AgentCore.Application.Office;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAttachmentReadObservation(TenantAttachmentContextSource Source, string ArgumentsJson);

public sealed partial class TenantAttachmentRuntimeContextService
{
    private async ValueTask<(View View, AttachmentRecord Source)> RequireRead(TenantRuntimeFrame frame, TenantAttachmentReadArguments args, CancellationToken ct, bool audit = false)
    {
        var view = await Load(frame, ct).ConfigureAwait(false);
        var current = await configurations.CurrentPolicyAsync(frame.Owner.TenantId!.Value, frame.Owner.AgentInstanceId, ct).ConfigureAwait(false);
        if (current.Agent.Instance.Revision != view.Agent.Instance.Revision || !TenantAttachmentReadContract.Allows(view.Run.Admission.Configuration!.Definition)
            || !TenantAttachmentReadContract.Allows(current.Definition)) throw new AccessDeniedException("capability_unavailable");
        await TenantAttachmentRules.PermissionsAsync(view.State, view.Agent.PrincipalId, frame.SessionId, args.AttachmentId, "attachments.read", iam, ct).ConfigureAwait(false);
        foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(frame.Owner.TenantId.Value, frame.SessionId)),
            ("attachments.read", TenantAttachmentRules.Scope(frame.Owner.TenantId.Value, args.AttachmentId)) })
        {
            var decision = audit ? await operations.Check(view.State, view.Run, key, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(view.State, view.Run.Admission.ExecutionContext!, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("attachment_read_review_required");
            if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        var source = await files.FindTenantAsync(frame.Owner.TenantId.Value, frame.SessionId, args.AttachmentId, ct).ConfigureAwait(false);
        if (source is not { State: AttachmentState.Bound, EntryId: not null } || source.SessionId != frame.SessionId || source.TenantId != frame.Owner.TenantId)
            throw new AccessDeniedException("attachment_source_unavailable");
        TenantAttachmentReadContract.Window(args, source.ContentType);
        return (view, source);
    }
    private async ValueTask ReadFence(TenantRuntimeFrame frame, View before, TenantAttachmentReadObservation observation, CancellationToken ct)
    {
        var after = await RequireRead(frame, TenantAttachmentReadContract.Parse(observation.ArgumentsJson), ct).ConfigureAwait(false);
        if (TenantAttachmentContextRules.Witness(after.Source, observation.Source.ProcessorVersion) != observation.Source)
            throw new AccessDeniedException("attachment_source_changed");
        await RequireSourcesAsync(after.View.Run, [observation.Source], ct).ConfigureAwait(false);
        await Fence(frame, before, ct).ConfigureAwait(false);
    }
    public async ValueTask<ToolExecutionResult> InspectReadAsync(TenantRuntimeFrame frame, string arguments, bool vision, int remainingBytes,
        IOfficeProcessor? office = null, CancellationToken ct = default)
    {
        var args = TenantAttachmentReadContract.Parse(arguments);
        try
        {
            var (view, source) = await RequireRead(frame, args, ct, audit: true).ConfigureAwait(false);
            var type = AttachmentMedia.NormalizeContentType(source.ContentType);
            var processed = source.Readable && (!AttachmentMedia.IsImage(type) || vision);
            var observation = new TenantAttachmentReadObservation(TenantAttachmentContextRules.Witness(source, processed ? processor.Version : null), TenantAttachmentReadContract.Canonical(args));
            string Envelope(object data) => JsonSerializer.Serialize(new { attachmentId = source.AttachmentId, displayName = source.DisplayName,
                contentType = source.ContentType, byteSize = source.ByteSize, untrusted = true, tenantAttachmentRead = true, attachmentRead = observation,
                inspection = data }, OfficeReadResults.Json);
            var budget = Math.Max(0, Math.Min(remainingBytes, ToolLimits.MaxOutputBytes));
            ToolExecutionResult result;
            if (!processed)
                result = new(Envelope(new { kind = AttachmentMedia.IsImage(type) ? "image" : "binary", contentProvided = false,
                    note = source.Readable ? "This model cannot inspect image content. Select a vision-capable model." : "This format has no automatic content reader. Original bytes require independently authorized download." }));
            else
            {
                if (type is OfficeLimits.DocumentMime or OfficeLimits.SpreadsheetMime && (office is null || source.ByteSize > OfficeLimits.MaxPackageBytes))
                    throw new AccessDeniedException(office is null ? "office_processing_unavailable" : "office_processing_limit");
                var bytes = await files.ReadTenantContentAsync(frame.Owner.TenantId!.Value, frame.SessionId, source.AttachmentId, ct).ConfigureAwait(false);
                TenantAttachmentRules.Content(source, bytes);
                if (type == OfficeLimits.DocumentMime)
                    result = new(OfficeReadResults.Document(office!.InspectDocument(bytes, args.Offset ?? 0, args.Limit ?? 100, ct), args.Offset ?? 0, budget, Envelope));
                else if (type == OfficeLimits.SpreadsheetMime)
                    result = new(args.Range is null ? OfficeReadResults.SparseWorkbook(office!.ReadWorkbookContent(bytes, args.Sheet, args.Cursor, ct), budget, Envelope)
                        : OfficeReadResults.Workbook(office!.InspectWorkbook(bytes, args.Sheet, args.Range, ct), budget, Envelope));
                else if (AttachmentMedia.IsReadableText(type))
                {
                    var text = new UTF8Encoding(false, true).GetString(bytes);
                    if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r' or '\f'))) throw new DecoderFallbackException();
                    result = new(ToolJsonResults.FitJsonWithContentField(budget, text, (content, truncated) => Envelope(new { content, truncated })));
                }
                else
                {
                    var content = await processor.ProcessSourceAsync(source, bytes, ct).ConfigureAwait(false);
                    if (content.AttachmentId != source.AttachmentId || content.DisplayName != source.DisplayName || content.ProcessorVersion != processor.Version)
                        throw new AccessDeniedException("attachment_source_changed");
                    if (content.Kind == AttachmentProcessKind.Image && content.StrippedImage is { Length: > 0 } image)
                        result = new(Envelope(new { kind = "image", contentProvided = true, contentType = content.ContentType }), [new ModelImageContent(content.ContentType, image, source.DisplayName)]);
                    else result = new(ToolJsonResults.FitJsonWithContentField(budget, content.Text ?? "", (text, truncated) => Envelope(new { content = text, truncated,
                        provenance = content.Provenance, failureCode = content.FailureCode, kind = content.Kind.ToString() })));
                }
            }
            await ReadFence(frame, view, observation, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetByteCount(result.Text) <= budget ? result : new(ToolJsonResults.FitToBudget(budget, """{"error":"output_limit"}"""));
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            if (frame.Owner.TenantId is { } tenant)
                await operations.RecordFailureAsync(new(tenant, frame.RunId), "attachments.read",
                    e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode, ExecutionAuthorizationPurpose.ToolBoundary, ct, frame.SessionId).ConfigureAwait(false);
            throw;
        }
        catch (DecoderFallbackException) { return new(ToolJsonResults.FitToBudget(remainingBytes, """{"error":"unsupported_reader","message":"This content is not valid UTF-8 text."}""")); }
        catch (Sessions.AgentCoreException e)
        {
            var code = new[] { "office_cursor_invalid", "office_revision_conflict", "office_processing_limit", "office_operation_unsupported", "invalid_office_package" }
                .FirstOrDefault(c => e.Code == c || e.Message.StartsWith(c + ":", StringComparison.Ordinal)) ?? "attachment_read_unavailable";
            return new(ToolJsonResults.FitToBudget(remainingBytes, JsonSerializer.Serialize(new { error = code, message = "The exact attachment observation is unavailable. Check current source and parameters." })));
        }
    }
    private static TenantAttachmentReadObservation? Observation(ModelMessage message)
    {
        if (message.Role != ModelRole.Tool || message.Name != ToolCatalog.AttachmentsRead) return null;
        try
        {
            using var doc = JsonDocument.Parse(message.Text);
            if (!doc.RootElement.TryGetProperty("tenantAttachmentRead", out var marker)) return null;
            if (marker.ValueKind != JsonValueKind.True) throw new AccessDeniedException("attachment_source_changed");
            var value = doc.RootElement.GetProperty("attachmentRead").Deserialize<TenantAttachmentReadObservation>(OfficeReadResults.Json) ?? throw new AccessDeniedException("attachment_source_changed");
            var args = TenantAttachmentReadContract.Parse(value.ArgumentsJson);
            if (value.Source is null || TenantAttachmentReadContract.Canonical(args) != value.ArgumentsJson || value.Source.AttachmentId != args.AttachmentId)
                throw new AccessDeniedException("attachment_source_changed");
            _ = TenantAttachmentContextRules.Merge(null, [value.Source]);
            return value;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { throw new AccessDeniedException("attachment_source_changed"); }
    }
    public async ValueTask RequireReadResultsAsync(AgentRun run, IReadOnlyList<ModelMessage> messages, CancellationToken ct = default)
    {
        foreach (var message in messages)
            if (Observation(message) is { } observation)
            {
                var frame = Frame(run); var current = await RequireRead(frame, TenantAttachmentReadContract.Parse(observation.ArgumentsJson), ct).ConfigureAwait(false);
                await ReadFence(frame, current.View, observation, ct).ConfigureAwait(false);
            }
    }
    public async ValueTask<List<ModelMessage>> RehydrateReadImagesAsync(AgentRun run, List<ModelMessage> messages, bool vision, CancellationToken ct = default)
    {
        await RequireReadResultsAsync(run, messages, ct).ConfigureAwait(false);
        var result = new List<ModelMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (Observation(message) is { } observation)
            {
                using var doc = JsonDocument.Parse(message.Text); var inspection = doc.RootElement.GetProperty("inspection");
                if (inspection.TryGetProperty("kind", out var kind) && kind.GetString() == "image"
                    && inspection.TryGetProperty("contentProvided", out var supplied) && supplied.ValueKind == JsonValueKind.True)
                {
                    if (!vision) throw new AccessDeniedException("attachment_vision_unavailable");
                    var rebuilt = await InspectReadAsync(Frame(run), observation.ArgumentsJson, true, ToolLimits.MaxOutputBytes, ct: ct).ConfigureAwait(false);
                    if (rebuilt.Parts is not { Count: > 0 }) throw new AccessDeniedException("attachment_source_changed");
                    result.Add(message with { Parts = rebuilt.Parts }); continue;
                }
            }
            result.Add(message);
        }
        return result;
    }
}
