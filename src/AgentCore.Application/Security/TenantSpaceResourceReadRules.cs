using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed record TenantSpaceResourceRead(string Tool, TenantWorkspaceReadArguments Arguments, string ObservationHash,
    IReadOnlyList<PreparedResourceReadMember> Members, Guid? RequestId, Guid? OperationId);

// Same-transaction output evidence. Verified source bytes are never reusable authority.
public static class TenantSpaceResourceReadRules
{
    public static IReadOnlyList<TenantSpaceResourceRead> Read(AgentRun run)
    {
        if (!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return [];
        var result = new List<TenantSpaceResourceRead>();
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && (TenantWorkspaceReadContract.IsRead(m.Name ?? "") || m.Name == ToolCatalog.RequestAuthorization)))
        {
            using var json = JsonDocument.Parse(message.Text); var body = json.RootElement; Guid? request = null, operation = null;
            if (message.Name == ToolCatalog.RequestAuthorization)
            {
                if (!body.TryGetProperty("workspaceObservation", out var observation) || observation.ValueKind == JsonValueKind.Null) continue;
                request = body.GetProperty("requestId").GetGuid(); operation = body.GetProperty("operationId").GetGuid(); body = observation;
            }
            if (!body.TryGetProperty("tenantWorkspaceRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            var witness = body.GetProperty("workspaceRead"); var tool = witness.GetProperty("tool").GetString()!;
            var args = witness.GetProperty("arguments").Deserialize<TenantWorkspaceReadArguments>() ?? throw new AccessDeniedException("resource_source_changed");
            args = TenantWorkspaceReadContract.Parse(tool, TenantWorkspaceReadContract.Canonical(tool, args));
            if (request is null && tool != message.Name) throw new AccessDeniedException("resource_source_changed");
            List<PreparedResourceReadMember> members = [];
            if (witness.TryGetProperty("resource", out var source))
            {
                var observed = source.Deserialize<TenantResourceSourceObservation>() ?? throw new AccessDeniedException("resource_source_changed");
                if (observed.Resource.SpaceSource is null) continue;
                if (observed.TenantId != run.Owner.TenantId || observed.InstanceId != run.AgentInstanceId || observed.SessionId != run.SessionId
                    || observed.Resource != run.Admission.Configuration!.Resources.SingleOrDefault(r => r.Key == observed.Resource.Key))
                    throw new AccessDeniedException("resource_source_changed");
                members.Add(new(observed.Resource.Key, observed.ResourceRevision));
            }
            else if (witness.TryGetProperty("resourceMembers", out var collection))
                members.AddRange((collection.Deserialize<PreparedResourceReadMember[]>() ?? throw new AccessDeniedException("resource_source_changed"))
                    .Where(m => SpaceResourceKeys.TryParse(m.Key, out _, out _)));
            if (members.Count == 0) continue;
            if (members.Count > 257 || members.Any(m => m.Revision < 1) || members.Select(m => m.Key).Distinct(StringComparer.Ordinal).Count() != members.Count
                || result.Count >= 100) throw new AccessDeniedException("resource_source_changed");
            result.Add(new(tool, args, witness.GetProperty("observationHash").GetString()!, members, request, operation));
        }
        return result;
    }
    public static async ValueTask RequireAsync(AgentRun run, TenantSpaceResourceRead source, TenantSecurityState state, TenantAgent agent,
        AgentDefinition baseline, IamAuthorizer iam, Func<string, long, ValueTask> requireCurrent,
        TenantApprovalRequest? request, TenantOperationReceipt? receipt, TenantApprovalDecision? decision, bool grantPresent, CancellationToken ct)
    {
        var pin = run.Admission.Configuration!; var current = InstanceSettingsResolver.ResolveForAgent(baseline, agent);
        if (!TenantTaskExecutionContract.Allows(run, current) || !TenantWorkspaceReadContract.Allows(pin.Definition, source.Tool)
            || !TenantWorkspaceReadContract.Allows(current, source.Tool) || agent.PrincipalId != run.Owner.ExecutorPrincipalId)
            throw new AccessDeniedException("resource_source_changed");
        foreach (var member in source.Members)
        {
            var resource = pin.Resources.SingleOrDefault(r => r.Key == member.Key);
            if (resource?.SpaceSource is not { } origin || origin.Revision != member.Revision || origin.SpaceId != pin.SpaceContext?.SpaceId
                || origin.TenantId != state.Tenant.TenantId || !(source.Tool == ToolCatalog.KnowledgeRetrieve
                    ? source.Arguments.Identity == member.Key && resource.Kind == AgentDefinitionResourceKind.Knowledge
                    : resource.VirtualPath == source.Arguments.Path || resource.VirtualPath.StartsWith(source.Arguments.Path + "/", StringComparison.Ordinal)))
                throw new AccessDeniedException("resource_source_changed");
            await requireCurrent(member.Key, member.Revision).ConfigureAwait(false);
        }
        if (source.RequestId is not null)
        {
            if (request?.Operation.WorkspaceRead is not { } saved || receipt is null || decision is null || grantPresent
                || request.Kind != ApprovalKind.Review || request.Status != TenantApprovalStatus.Approved
                || source.RequestId != request.RequestId || source.OperationId != request.Operation.OperationId
                || saved.ToolName != source.Tool || saved.ArgumentsJson != TenantWorkspaceReadContract.Canonical(source.Tool, source.Arguments)
                || saved.ObservationHash != source.ObservationHash || request.TenantId != state.Tenant.TenantId || request.SessionId != run.SessionId
                || request.Operation.InstanceId != run.AgentInstanceId || request.ExecutorPrincipalId != agent.PrincipalId
                || request.RootActivationId != run.Admission.ExecutionContext!.RootActivationId
                || request.OriginalInitiatorPrincipalId != run.Admission.ExecutionContext.OriginalInitiatorPrincipalId
                || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantWorkspaceReadOperationRules.Reason
                || receipt.RequestId != request.RequestId || receipt.OperationId != source.OperationId || receipt.ActionHash != request.Operation.ActionHash
                || receipt.TenantId != request.TenantId || receipt.InstanceId != run.AgentInstanceId || receipt.SessionId != run.SessionId
                || receipt.RootActivationId != request.RootActivationId || receipt.ExecutorPrincipalId != agent.PrincipalId
                || decision.TenantId != request.TenantId || decision.RequestId != request.RequestId || decision.Status != TenantApprovalStatus.Approved
                || decision.ActionHash != request.Operation.ActionHash || decision.RequestRevision != request.Revision)
                throw new AccessDeniedException("resource_source_changed");
            var savedMembers = saved.ResourceMembersJson is null ? [new PreparedResourceReadMember(saved.ResourceKey!, saved.ResourceRevision!.Value)] : TenantApprovalRules.ResourceReadMembers(saved);
            if (source.Members.Any(m => !savedMembers.Contains(m))) throw new AccessDeniedException("resource_source_changed");
            await TenantWorkspaceReadOperationRules.ReviewerAsync(state, agent.Instance.InstanceId, saved, TenantWorkspaceRules.Context(request.Operation), request.OriginalInitiatorPrincipalId,
                decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
        }
        foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(state.Tenant.TenantId, run.SessionId)),
            ("agents.read", AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D"))) }
            .Concat(source.Members.Select(m => ("resources.read", TenantResourceScopes.Source(state.Tenant.TenantId, run.AgentInstanceId, m.Key)))))
        {
            if (!(await iam.CheckOwnedTenantAsync(state, agent.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("resource_source_changed");
            if (source.RequestId is null && await TenantSandboxReadOperationRules.NeedsReviewAsync(state, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false))
                throw new AccessDeniedException("resource_read_review_required");
        }
    }
}
