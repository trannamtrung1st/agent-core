using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;

public sealed class TenantAutomationReadService(ITenantAutomationReadStore store, TenantOperationAuthorizer operations)
{
    public async ValueTask<TenantAutomationReadObservation> PrepareAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        CancellationToken ct, PreparedAutomationRead? expected = null) => await store.ObserveTenantAutomationReadAsync(reference, generation, tool, arguments, expected, ct: ct).ConfigureAwait(false);
    public async ValueTask<OperationAuthorizationDecision> DecisionAsync(TenantAutomationReadObservation value, CancellationToken ct)
    {
        var reader = value.Reader; var sources = TenantAutomationReadRules.Sources(value.Value);
        if (sources.Length == 0) return new(OperationDecisionKind.Allowed, "no_readable_automations", "automations.read", AccessScope.ForTenant(reader.Security.Tenant.TenantId));
        var primary = TenantAutomationRules.Scope(reader.Security.Tenant.TenantId, sources[0].AutomationId); var review = false;
        foreach (var (key, scope) in TenantAutomationReadRules.Targets(reader, value.Value))
        {
            var decision = await operations.Check(reader.Security, reader.Run, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.Denied || decision.ApprovalKind == ApprovalKind.ExceptionalGrant) return new(OperationDecisionKind.Denied, "automation_read_required", "automations.read", primary);
            review |= decision.Decision == OperationDecisionKind.RequiresApproval;
        }
        if (review)
        {
            try { await store.RequireTenantAutomationReadSourcesAsync(new(reader.Security.Tenant.TenantId, reader.Run.AgentRunId), [Reference(value.Value, reader.Security.Tenant.TenantId)], reader.Run.Claim?.Generation, ct).ConfigureAwait(false);
                return new(OperationDecisionKind.Allowed, "exact_read_consent", "automations.read", primary); }
            catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return await operations.Check(reader.Security, reader.Run, "automations.read", primary, ct, review).ConfigureAwait(false);
    }
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct)
    {
        var reference = new TenantRunReference(frame.Owner.TenantId!.Value, frame.RunId);
        var before = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
        var decision = await DecisionAsync(before, ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed) return JsonSerializer.Serialize(new { decision = decision.Decision == OperationDecisionKind.Denied ? "denied" : "requiresApproval", reasonCode = decision.ReasonCode, readAuthorized = false });
        var after = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct, before.Value).ConfigureAwait(false);
        if (after.Reader.Security.Tenant.Revision != before.Reader.Security.Tenant.Revision || after.Reader.Run.Revision != before.Reader.Run.Revision || after.Reader.Agent.Instance.Revision != before.Reader.Agent.Instance.Revision)
            throw new SecurityConflictException("automation_source_changed");
        return Serialize(after.Value, reference.TenantId, budget);
    }
    public static TenantAutomationReadReference Reference(PreparedAutomationRead value, Guid tenant) => new(tenant, value.ToolName, value.ArgumentsJson, value.SourcesJson, value.EventSourcesJson, value.ObservationHash);
    public static string Serialize(PreparedAutomationRead value, Guid tenant, int budget)
    {
        var body = new JsonObject { ["observation"] = JsonNode.Parse(value.ObservationJson), ["boundedObservation"] = true, ["untrustedConfiguration"] = true };
        if (TenantAutomationReadRules.Sources(value).Length > 0) { body["tenantAutomationRead"] = true; body["automationReadSource"] = JsonSerializer.SerializeToNode(Reference(value, tenant)); }
        var text = body.ToJsonString(); return Encoding.UTF8.GetByteCount(text) <= Math.Min(16000, budget) ? text : AgentCore.Application.Execution.AgentRunToolCallCheckpoint.FinishRequiredResult(budget);
    }
    public async ValueTask<bool> IndependentAsync(TenantApprovalRequest request, TenantAutomationReadObservation observed, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in TenantAutomationReadRules.Targets(observed.Reader, observed.Value))
            if (TenantOperationPolicyService.Effective(observed.Reader.Security, PermissionCatalog.Get(key)).IndependentReview
                && (key == request.Permission && scope == TenantApprovalRules.Scope(request.TenantId, request.Operation)
                    || await TenantSandboxReadOperationRules.NeedsReviewAsync(observed.Reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public async ValueTask<string> InspectApprovedAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, int budget, CancellationToken ct)
    {
        if (request.Operation.AutomationRead is not { } value || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantAutomationReadRules.Completed
            || receipt.RequestId != request.RequestId || receipt.TenantId != frame.Owner.TenantId || receipt.RunId != frame.RunId || receipt.ActionHash != request.Operation.ActionHash)
            throw new AccessDeniedException("approval_unavailable");
        var observed = await PrepareAsync(new(frame.Owner.TenantId!.Value, frame.RunId), frame.Generation, value.ToolName, value.ArgumentsJson, ct, value).ConfigureAwait(false);
        await store.RequireTenantAutomationReadSourcesAsync(new(frame.Owner.TenantId.Value, frame.RunId), [Reference(value, frame.Owner.TenantId.Value)], frame.Generation, ct).ConfigureAwait(false);
        return Serialize(observed.Value, frame.Owner.TenantId.Value, budget);
    }
    public ValueTask RequireAsync(AgentCore.Domain.Conversation.AgentRun run, CancellationToken ct) => store.RequireTenantAutomationReadSourcesAsync(new(run.Owner.TenantId!.Value, run.AgentRunId), TenantAutomationReadReferences.Read(run.Checkpoint), run.Claim?.Generation, ct);
    public ValueTask<TenantAutomationReadObservation> InspectionAsync(TenantApprovalRequest request, Guid human, CancellationToken ct) => store.ObserveTenantAutomationReadAsync(new(request.TenantId, request.RunId), null,
        request.Operation.AutomationRead!.ToolName, request.Operation.AutomationRead.ArgumentsJson, request.Operation.AutomationRead, human, ct);
    public static async ValueTask ReviewerAsync(TenantApprovalRequest request, TenantAutomationReadObservation observed, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (request.Kind != ApprovalKind.Review) throw new AccessDeniedException("approver_unqualified");
        await TenantAutomationReadRules.RawAsync(observed.Reader, observed.Value, iam, ct, human).ConfigureAwait(false);
        foreach (var (key, scope) in TenantAutomationReadRules.Targets(observed.Reader, observed.Value))
        {
            var primary = key == request.Permission && scope == TenantApprovalRules.Scope(request.TenantId, request.Operation);
            if (!primary && !await TenantSandboxReadOperationRules.NeedsReviewAsync(observed.Reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(observed.Reader.Security, PermissionCatalog.Get(key)).IndependentReview && request.OriginalInitiatorPrincipalId == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(observed.Reader.Security, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("approver_unqualified");
        }
    }
}
