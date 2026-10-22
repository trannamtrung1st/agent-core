using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;

public sealed class TenantManagementReadService(ITenantManagementReadStore store, TenantOperationAuthorizer operations)
{
    public ValueTask<TenantManagementReadObservation> PrepareAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        CancellationToken ct, PreparedManagementRead? expected = null) => store.ObserveTenantManagementReadAsync(reference, generation, tool, arguments, expected, ct: ct);
    public ValueTask<bool> IndependentAsync(TenantApprovalRequest request, TenantManagementReadObservation observed, IamAuthorizer iam, CancellationToken ct)
        => ValueTask.FromResult(TenantOperationPolicyService.Effective(observed.Reader.Security, PermissionCatalog.Get("agents.read")).IndependentReview);
    public async ValueTask<OperationAuthorizationDecision> DecisionAsync(TenantManagementReadObservation observed, CancellationToken ct)
    {
        var reader = observed.Reader; var scope = TenantManagementReadRules.Scope(reader);
        var decision = await operations.Check(reader.Security, reader.Run, "agents.read", scope, ct).ConfigureAwait(false);
        if (decision.ApprovalKind == ApprovalKind.ExceptionalGrant) return new(OperationDecisionKind.Denied, "management_read_required", "agents.read", scope);
        if (decision.Decision == OperationDecisionKind.RequiresApproval)
        {
            try { await store.RequireTenantManagementReadSourcesAsync(new(reader.Security.Tenant.TenantId, reader.Run.AgentRunId), [Reference(observed.Value, reader.Security.Tenant.TenantId)], reader.Run.Claim?.Generation, ct).ConfigureAwait(false);
                return new(OperationDecisionKind.Allowed, "exact_read_consent", "agents.read", scope); }
            catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return decision;
    }
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct)
    {
        var reference = new TenantRunReference(frame.Owner.TenantId!.Value, frame.RunId);
        var before = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
        var decision = await DecisionAsync(before, ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed) return JsonSerializer.Serialize(new { decision = decision.Decision == OperationDecisionKind.Denied ? "denied" : "requiresApproval", reasonCode = decision.ReasonCode, readAuthorized = false });
        var after = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct, before.Value).ConfigureAwait(false);
        if (after.Reader.Security.Tenant.Revision != before.Reader.Security.Tenant.Revision || after.Reader.Run.Revision != before.Reader.Run.Revision || after.Reader.Agent.Instance.Revision != before.Reader.Agent.Instance.Revision)
            throw new SecurityConflictException("management_source_changed");
        return Serialize(after.Value, reference.TenantId, budget);
    }
    public static TenantManagementReadReference Reference(PreparedManagementRead value, Guid tenant) => new(tenant, value.ToolName, value.ArgumentsJson,
        value.InstanceRevision, value.ConfigurationHash, value.StableConfigurationHash, value.ObservationHash);
    public static string Serialize(PreparedManagementRead value, Guid tenant, int budget)
    {
        var body = new JsonObject { ["observation"] = JsonNode.Parse(value.ObservationJson), ["boundedObservation"] = true, ["untrustedConfiguration"] = true,
            ["tenantManagementRead"] = true, ["managementReadSource"] = JsonSerializer.SerializeToNode(Reference(value, tenant)) };
        var text = body.ToJsonString(); return Encoding.UTF8.GetByteCount(text) <= Math.Min(22000, budget) ? text : AgentCore.Application.Execution.AgentRunToolCallCheckpoint.FinishRequiredResult(budget);
    }
    public ValueTask RequireAsync(AgentCore.Domain.Conversation.AgentRun run, CancellationToken ct) => store.RequireTenantManagementReadSourcesAsync(new(run.Owner.TenantId!.Value, run.AgentRunId), TenantManagementReadReferences.Read(run.Checkpoint), run.Claim?.Generation, ct);
    public ValueTask<TenantManagementReadObservation> InspectionAsync(TenantApprovalRequest request, Guid human, CancellationToken ct) => store.ObserveTenantManagementReadAsync(new(request.TenantId, request.RunId), null,
        request.Operation.ManagementRead!.ToolName, request.Operation.ManagementRead.ArgumentsJson, request.Operation.ManagementRead, human, ct);
    public static async ValueTask ReviewerAsync(TenantApprovalRequest request, TenantManagementReadObservation observed, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (request.Kind != ApprovalKind.Review) throw new AccessDeniedException("approver_unqualified");
        await TenantManagementReadRules.RawAsync(observed.Reader, iam, ct, human).ConfigureAwait(false);
        if (TenantOperationPolicyService.Effective(observed.Reader.Security, PermissionCatalog.Get("agents.read")).IndependentReview && request.OriginalInitiatorPrincipalId == human)
            throw new AccessDeniedException("independent_reviewer_required");
        if (!(await iam.CheckOwnedTenantAsync(observed.Reader.Security, human, PermissionCatalog.Get("approvals.approve"), TenantManagementReadRules.Scope(observed.Reader), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("approver_unqualified");
    }
    public async ValueTask<string> InspectApprovedAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, int budget, CancellationToken ct)
    {
        if (request.Operation.ManagementRead is not { } value || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantManagementReadRules.Completed
            || receipt.RequestId != request.RequestId || receipt.TenantId != frame.Owner.TenantId || receipt.RunId != frame.RunId || receipt.ActionHash != request.Operation.ActionHash)
            throw new AccessDeniedException("approval_unavailable");
        var observed = await PrepareAsync(new(frame.Owner.TenantId!.Value, frame.RunId), frame.Generation, value.ToolName, value.ArgumentsJson, ct, value).ConfigureAwait(false);
        await store.RequireTenantManagementReadSourcesAsync(new(frame.Owner.TenantId.Value, frame.RunId), [Reference(value, frame.Owner.TenantId.Value)], frame.Generation, ct).ConfigureAwait(false);
        return Serialize(observed.Value, frame.Owner.TenantId.Value, budget);
    }
}
