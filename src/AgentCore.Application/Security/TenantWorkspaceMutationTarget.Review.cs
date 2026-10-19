using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantWorkspaceMutationTarget
{
    public async ValueTask RequireSavedAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default)
    {
        var value = request.Operation.WorkspaceMutation ?? throw new SecurityConflictException("operation_mismatch");
        var reference = new TenantRunReference(request.TenantId, run.AgentRunId);
        var (current, state) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
        if (current.Revision != run.Revision) throw new SecurityConflictException("stale_revision");
        var agent = await configuration.CurrentPolicyAsync(request.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var source = run.Admission.ApprovalResume is { } resume ? await operations.FindRunAsync(new(request.TenantId, resume.SourceRunId), ct).ConfigureAwait(false) : null;
        TenantApprovalRules.Validate(request, current, agent.Agent, source: source);
        TenantWorkspaceMutationOperationRules.Require(request, current, agent.Agent, agent.Definition,
            await sessions.FindTenantSessionAsync(request.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable"), source);
        await TenantWorkspaceMutationOperationRules.ExecutorAsync(state, request, iam, ct).ConfigureAwait(false);
        var observed = await ObserveAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty),
            new("saved-workspace-mutation", value.ToolName, value.ArgumentsJson), false, ct).ConfigureAwait(false);
        await TenantWorkspaceMutationOperationRules.ExecutorAsync(observed.State, request, iam, ct).ConfigureAwait(false);
        if (observed.Run.Revision != run.Revision || observed.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || observed.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision || observed.ObservationHash != value.ObservationHash)
            throw new SecurityConflictException("workspace_source_changed");
    }
}
