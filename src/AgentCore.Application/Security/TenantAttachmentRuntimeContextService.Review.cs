using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAttachmentPreparedRead(AgentRun Run, TenantSecurityState State, TenantAgent Agent,
    PreparedAttachmentRead Read, TenantApprovalSnapshot Snapshot, bool ReviewRequired);

public sealed partial class TenantAttachmentRuntimeContextService
{
    private async ValueTask<bool> ReadVision(View view, CancellationToken ct)
    {
        var run = view.Run; var models = await modelCatalogs.ReadAsync(view.State, ct).ConfigureAwait(false);
        var model = models.Get(run.PinnedModel.OfferingId);
        if (model is null || !model.Tools || model.ConnectionId != run.PinnedModel.ConnectionId || model.ExternalModelId != run.PinnedModel.ExternalModelId || model.Route != run.PinnedModel.Binding?.Route)
            throw new AccessDeniedException("capability_unavailable");
        return model.Vision;
    }
    private PreparedAttachmentRead WitnessRead(AttachmentRecord source, TenantAttachmentReadArguments args, bool vision) =>
        TenantAttachmentReadRules.Witness(source, TenantAttachmentReadContract.Canonical(args),
            source.Readable && (!AttachmentMedia.IsImage(source.ContentType) || vision) ? processor.Version : null, vision);
    private static TenantAttachmentContextSource Source(PreparedAttachmentRead read) =>
        new(read.AttachmentId, read.EntryId, read.DisplayName, read.ContentType, read.ByteSize, read.Sha256Hex, read.ProcessorVersion);
    private async ValueTask MetadataFence(TenantRuntimeFrame frame, View before, AttachmentRecord source, TenantAttachmentReadArguments args, CancellationToken ct, bool live = true)
    {
        var after = await RequireRead(frame, args, ct, approved: true, live: live).ConfigureAwait(false);
        if (after.Source != source) throw new AccessDeniedException("attachment_source_changed");
        await Fence(frame, before, ct, live).ConfigureAwait(false);
    }
    public async ValueTask<TenantAttachmentPreparedRead> PrepareInspectionAsync(TenantRuntimeFrame frame, string arguments, CancellationToken ct = default)
    {
        var args = TenantAttachmentReadContract.Parse(arguments);
        var (view, source) = await RequireRead(frame, args, ct, approved: true).ConfigureAwait(false);
        var value = WitnessRead(source, args, await ReadVision(view, ct).ConfigureAwait(false)); var review = false;
        foreach (var (permission, scope) in TenantAttachmentReadRules.Targets(view.State.Tenant.TenantId, value))
            review |= await TenantSandboxReadOperationRules.NeedsReviewAsync(view.State, view.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, permission, scope, iam, ct).ConfigureAwait(false);
        await MetadataFence(frame, view, source, args, ct).ConfigureAwait(false);
        return new(view.Run, view.State, view.Agent, value, new(view.Agent.Instance.Persona.Name), review);
    }
    public async ValueTask RequireInspectionAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default)
    {
        var value = request.Operation.AttachmentRead ?? throw new AccessDeniedException("operation_mismatch"); var args = TenantAttachmentReadContract.Parse(value.ArgumentsJson);
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty);
        var (view, source) = await RequireRead(frame, args, ct, approved: true, live: false).ConfigureAwait(false);
        if (view.Run.Revision != run.Revision || view.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || view.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision || value.Vision && !await ReadVision(view, ct).ConfigureAwait(false)
            || WitnessRead(source, args, value.Vision) != value) throw new SecurityConflictException("attachment_source_changed");
        await MetadataFence(frame, view, source, args, ct, live: false).ConfigureAwait(false);
    }
    public async ValueTask RequireInspectionReadAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct)
    {
        var value = request.Operation.AttachmentRead!;
        await TenantAttachmentReadRules.PermissionsAsync(state, value, human, iam, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(request.TenantId, request.SessionId, ct).ConfigureAwait(false);
        var actual = await files.FindTenantAsync(request.TenantId, request.SessionId, value.AttachmentId, ct).ConfigureAwait(false);
        if (session is null || session.Owner.AgentInstanceId != request.Operation.InstanceId || session.Owner.ExecutorPrincipalId != request.ExecutorPrincipalId
            || actual is not { State: AttachmentState.Bound } || TenantAttachmentReadRules.Witness(actual, value.ArgumentsJson, value.ProcessorVersion, value.Vision) != value)
            throw new AccessDeniedException("approval_unavailable");
    }
    public ValueTask RequireInspectionReviewerAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct) =>
        TenantAttachmentReadRules.ReviewerAsync(state, request.Operation.AttachmentRead!, request.OriginalInitiatorPrincipalId, human, iam, ct);
    public ValueTask<bool> InspectionIndependentAsync(TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct) =>
        TenantAttachmentReadRules.IndependentAsync(state, request.Operation.AttachmentRead!, request.OriginalInitiatorPrincipalId, iam, ct);
    private async ValueTask RequireApprovedReadData(View current, AttachmentRecord source, TenantApprovalRequest request, TenantOperationReceipt receipt, CancellationToken ct)
    {
        if (request.Operation.AttachmentRead is not { } value || request.Kind != ApprovalKind.Review || request.Status != TenantApprovalStatus.Approved
            || request.SessionId != current.Run.SessionId || request.TenantId != current.Run.Owner.TenantId || request.ExecutorPrincipalId != current.Run.Owner.ExecutorPrincipalId
            || request.Operation.InstanceId != current.Run.AgentInstanceId || request.RootActivationId != current.Run.Admission.ExecutionContext!.RootActivationId
            || request.OriginalInitiatorPrincipalId != current.Run.Admission.ExecutionContext.OriginalInitiatorPrincipalId
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantAttachmentReadRules.Reason || receipt.TenantId != request.TenantId
            || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId || receipt.ActionHash != request.Operation.ActionHash
            || receipt.InstanceId != request.Operation.InstanceId || receipt.SessionId != request.SessionId || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId
            || receipt.RootActivationId != request.RootActivationId || value.Vision && !await ReadVision(current, ct).ConfigureAwait(false)
            || WitnessRead(source, TenantAttachmentReadContract.Parse(value.ArgumentsJson), value.Vision) != value
            || await approvals.FindApprovalAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) != request
            || await effects.FindOperationReceiptAsync(request.TenantId, receipt.OperationId, ct).ConfigureAwait(false) != receipt
            || await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) is not null)
            throw new AccessDeniedException("attachment_source_changed");
        var decision = await effects.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        if (decision.RequestRevision != request.Revision || decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != request.Operation.ActionHash)
            throw new AccessDeniedException("attachment_source_changed");
        await RequireInspectionReviewerAsync(request, current.State, decision.HumanPrincipalId, ct).ConfigureAwait(false);
    }
    public async ValueTask<string> InspectApprovedForRunAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, int remainingBytes, CancellationToken ct = default)
    {
        var value = request.Operation.AttachmentRead ?? throw new AccessDeniedException("operation_mismatch");
        var (view, source) = await RequireRead(frame, TenantAttachmentReadContract.Parse(value.ArgumentsJson), ct, approved: true).ConfigureAwait(false);
        await RequireApprovedReadData(view, source, request, receipt, ct).ConfigureAwait(false);
        await RequireInspectionAsync(request, view.Run, ct).ConfigureAwait(false);
        if (receipt.RunId != frame.RunId || view.Run.SideEffect.Disposition != AgentRunSideEffectDisposition.Succeeded
            || view.Run.SideEffect.ToolCallId != "operation:" + receipt.OperationId.ToString("D") || view.Run.SideEffect.ActionHash != receipt.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        TenantRunContinuationService.ValidateAuthorizationCheckpoint(view.Run, request, requirePending: true);
        var result = await InspectReadCore(frame, value.ArgumentsJson, value.Vision, remainingBytes, officeReader, ct, approved: true).ConfigureAwait(false);
        var after = await RequireRead(frame, TenantAttachmentReadContract.Parse(value.ArgumentsJson), ct, approved: true).ConfigureAwait(false);
        await RequireApprovedReadData(after.View, after.Source, request, receipt, ct).ConfigureAwait(false);
        return result.Text;
    }
    private async ValueTask<(TenantApprovalRequest Request, TenantOperationReceipt Receipt, TenantAttachmentReadObservation Observation)?> ApprovedEnvelope(AgentRun run, ModelMessage message, CancellationToken ct)
    {
        if (message.Role != ModelRole.Tool || message.Name != ToolCatalog.RequestAuthorization) return null;
        try
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (!root.TryGetProperty("attachmentObservation", out var data) || data.ValueKind == JsonValueKind.Null) return null;
            if (!root.TryGetProperty("requestId", out var id) || !id.TryGetGuid(out var requestId) || !root.TryGetProperty("operationId", out var op) || !op.TryGetGuid(out var operationId))
                throw new AccessDeniedException("attachment_source_changed");
            var observation = ObservationPayload(data.GetRawText()) ?? throw new AccessDeniedException("attachment_source_changed");
            var request = await approvals.FindApprovalAsync(run.Owner.TenantId!.Value, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            var receipt = await effects.FindOperationReceiptAsync(request.TenantId, operationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            var value = request.Operation.AttachmentRead ?? throw new AccessDeniedException("operation_mismatch");
            if (observation.ArgumentsJson != value.ArgumentsJson || observation.Source != Source(value)) throw new AccessDeniedException("attachment_source_changed");
            var current = await RequireRead(Frame(run), TenantAttachmentReadContract.Parse(value.ArgumentsJson), ct, approved: true).ConfigureAwait(false);
            await RequireApprovedReadData(current.View, current.Source, request, receipt, ct).ConfigureAwait(false);
            await ReadFence(Frame(run), current.View, observation, ct, approved: true).ConfigureAwait(false);
            return (request, receipt, observation);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { throw new AccessDeniedException("attachment_source_changed"); }
    }
    private async ValueTask RequireStoredApprovedReadsAsync(AgentRun run, IReadOnlyList<ModelMessage> messages, CancellationToken ct)
    { foreach (var message in messages) _ = await ApprovedEnvelope(run, message, ct).ConfigureAwait(false); }
    private async ValueTask<ModelMessage?> RehydrateApprovedImageAsync(AgentRun run, ModelMessage message, bool vision, CancellationToken ct)
    {
        if (await ApprovedEnvelope(run, message, ct).ConfigureAwait(false) is not { } approved) return null;
        using var doc = JsonDocument.Parse(message.Text); var data = doc.RootElement.GetProperty("attachmentObservation").GetProperty("inspection");
        if (!data.TryGetProperty("kind", out var kind) || kind.GetString() != "image" || !data.TryGetProperty("contentProvided", out var supplied) || supplied.ValueKind != JsonValueKind.True) return null;
        if (!vision || !approved.Request.Operation.AttachmentRead!.Vision) throw new AccessDeniedException("attachment_vision_unavailable");
        var rebuilt = await InspectReadCore(Frame(run), approved.Observation.ArgumentsJson, true, ToolLimits.MaxOutputBytes, officeReader, ct, approved: true).ConfigureAwait(false);
        if (rebuilt.Parts is not { Count: > 0 }) throw new AccessDeniedException("attachment_source_changed");
        _ = await ApprovedEnvelope(run, message, ct).ConfigureAwait(false);
        return message with { Parts = rebuilt.Parts };
    }
}
