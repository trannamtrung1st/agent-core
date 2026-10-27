using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxResultRead(SandboxCommandResult? Result, string? UnavailableReason = null);

public sealed partial class TenantSandboxApprovedOperationService
{
    // Stored evidence for this exact continued operation, with current Run/requester/executor/source scope.
    // No SDK contact, readiness dependency, new dispatch authority or mutable model-selected receipt ID.
    public async ValueTask<TenantSandboxResultRead> ReadResultAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, CancellationToken ct = default)
    {
        try
        {
            if (request.Operation.Kind != TenantOperationKind.SandboxCommand) throw new SecurityConflictException("sandbox_approval_mismatch");
            var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
            var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
            var source = run.Admission.ApprovalResume is { } resume ? await runs.FindTenantRunAsync(tenant, resume.SourceRunId, ct).ConfigureAwait(false) : null;
            TenantRunContinuationRules.Require(run, request, source);
            var target = await targets.ResolveAsync(run, request.Operation.SandboxCommand!.ProfileId, ct, storedResult: true).ConfigureAwait(false);
            TenantRuntimeRules.Require(frame, target.Session.Owner, run, time.GetUtcNow());
            var execution = await runtime.FindTenantSandboxOperationAsync(tenant, frame.SessionId, frame.RunId, "operation:" + request.Operation.OperationId.ToString("D"), ct).ConfigureAwait(false);
            if (receipt.TenantId != tenant || receipt.RunId != frame.RunId || receipt.SessionId != frame.SessionId || receipt.InstanceId != frame.Owner.AgentInstanceId
                || receipt.ExecutorPrincipalId != frame.Owner.ExecutorPrincipalId || receipt.RootActivationId != request.RootActivationId
                || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId || receipt.ActionHash != request.Operation.ActionHash
                || execution is null || execution.TenantId != tenant || execution.AgentRunId != frame.RunId || execution.SessionId != frame.SessionId
                || execution.AgentInstanceId != frame.Owner.AgentInstanceId || execution.ExecutorPrincipalId != frame.Owner.ExecutorPrincipalId
                || execution.ApprovalRequestId != request.RequestId || execution.OperationId != receipt.OperationId || execution.ActionHash != receipt.ActionHash
                || execution.ProfileId != request.Operation.SandboxCommand.ProfileId || execution.ProfileRevision != request.Operation.SandboxCommand.ProfileRevision)
                throw new SecurityConflictException("sandbox_approval_mismatch");
            var (after, latest) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
            TenantRuntimeRules.Require(frame, target.Session.Owner, after, time.GetUtcNow());
            if (latest.Tenant.Revision != state.Tenant.Revision || after.Revision != run.Revision) throw new SecurityConflictException("stale_revision");
            return new(new(execution.Id, execution.State.ToString(), execution.ExitCode, execution.Stdout, execution.Stderr,
                execution.Truncated, execution.DurationMs ?? 0, execution.FilesystemEffectsMayHaveOccurred, execution.DiagnosticId, execution.Background,
                ProfileId: execution.ProfileId, ProfileRevision: execution.ProfileRevision, Generation: execution.Generation,
                Phase: execution.Phase, FailureCategory: execution.FailureCategory, CancellationReason: execution.CancellationReason,
                TerminationConfirmed: execution.TerminationConfirmed, GenerationRecycled: execution.GenerationRecycled, RemoteTerminationConfirmed: execution.RemoteTerminationConfirmed));
        }
        catch (AccessDeniedException error) { return new(null, error.ReasonCode); }
        catch (SecurityConflictException error) { return new(null, error.ReasonCode); }
    }
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default)
    {
        if (run.Checkpoint is null || !AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && m.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty("sandbox", out var data)
                || data.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (!root.TryGetProperty("operationId", out var id) || !id.TryGetGuid(out var operationId)) throw new AccessDeniedException("sandbox_source_unavailable");
            var source = await TenantSandboxRetainedSources.ReadAsync(run, operationId, operations, approvals, decisions, ct);
            if (source.Request.Operation.SandboxCommand is null) throw new AccessDeniedException("sandbox_source_unavailable");
            await targets.RequireRetainedCommandAsync(source.Request, source.Run, source.Decision.HumanPrincipalId, ct);
            await TenantSandboxRetainedSources.FenceAsync(run, source.State.Tenant.Revision, operations, ct);
        }
    }

}
