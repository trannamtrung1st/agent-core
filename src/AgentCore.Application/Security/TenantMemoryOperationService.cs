using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantMemoryOperationService(ITenantMemoryOperationStore memories, ITenantLearnedMemoryStore sources, TenantOperationAuthorizer operations,
    IamAuthorizer iam, TimeProvider time)
{
    public async ValueTask<TenantMemoryOperationObservation> PrepareAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        CancellationToken ct = default, PreparedMemoryOperation? expected = null)
    {
        var args = TenantMemoryToolContract.Parse(tool, arguments);
        var observation = await memories.ObserveTenantMemoryOperationAsync(reference, generation, args, expected, ct: ct).ConfigureAwait(false);
        if (generation is not null && observation.Reader.Run.Claim?.LeaseExpiresAtUtc <= time.GetUtcNow()) throw new SecurityConflictException("execution_claim_expired");
        return observation;
    }
    public async ValueTask<OperationAuthorizationDecision> DecisionAsync(TenantMemoryOperationObservation observed, CancellationToken ct = default)
    {
        var reader = observed.Reader; var primaryKey = observed.Arguments.ReadOnly ? "memory.read" : "memory.manage";
        var primary = AccessScope.ForResource(reader.Security.Tenant.TenantId, "agent", reader.Run.AgentInstanceId.ToString("D")); var review = !observed.Arguments.ReadOnly;
        foreach (var (key, scope) in TenantMemoryOperationRules.Targets(reader, observed.Items, !observed.Arguments.ReadOnly))
        {
            if (key == primaryKey && scope == primary) continue;
            // Consent never fills a missing raw read or a secondary mutation permission.
            var decision = await operations.Check(reader.Security, reader.Run, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.Denied || decision.ApprovalKind == ApprovalKind.ExceptionalGrant)
                return new(OperationDecisionKind.Denied, "memory_source_authority_required", primaryKey, primary);
            review |= decision.Decision == OperationDecisionKind.RequiresApproval;
        }
        var result = await operations.Check(reader.Security, reader.Run, primaryKey, primary, ct, review).ConfigureAwait(false);
        if (observed.Arguments.ReadOnly && observed.Items.Count > 0 && result.Decision == OperationDecisionKind.RequiresApproval)
        {
            try
            {
                await sources.RequireTenantMemorySourcesAsync(new(reader.Security.Tenant.TenantId, reader.Run.AgentRunId),
                    observed.Items.Select(i => new TenantMemorySource(i.TenantId, i.MemoryId, i.ContentHash, i.Status == MemoryItemStatus.Deleted)).ToArray(), reader.Run.Claim?.Generation, ct).ConfigureAwait(false);
                return result with { Decision = OperationDecisionKind.Allowed, ReasonCode = "exact_read_consent", ApprovalKind = null };
            }
            catch (AccessDeniedException) { }
            catch (SecurityConflictException) { }
        }
        return result;
    }
    public async ValueTask<string> InspectAsync(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct = default)
    {
        var reference = new TenantRunReference(frame.Owner.TenantId!.Value, frame.RunId);
        var before = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
        if (before.ExistingResult is { } existing)
        {
            try
            {
                var witness = new TenantMemorySource(existing.TenantId, existing.MemoryId, existing.ContentHash, true);
                await sources.RequireTenantMemorySourcesAsync(reference, [witness], frame.Generation, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { decision = "allowed", applied = false, alreadyCompleted = true, memoryId = existing.MemoryId,
                    statusObserved = existing.Status.ToString(), observedAtUtc = time.GetUtcNow(), tenantMemoryRead = true, memorySources = new[] { witness } });
            }
            catch (AccessDeniedException) { return """{"decision":"requiresApproval","reasonCode":"memory_read_review_required","applied":false}"""; }
        }
        var decision = await DecisionAsync(before, ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed || !before.Arguments.ReadOnly)
            return JsonSerializer.Serialize(new { decision = decision.Decision == OperationDecisionKind.Denied || before.Arguments.ReadOnly ? JsonNamingPolicy.CamelCase.ConvertName(decision.Decision.ToString()) : "requiresApproval",
                reasonCode = decision.ReasonCode, applied = false });
        var after = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct, before.Value).ConfigureAwait(false);
        if (before.Reader.Run.Revision != after.Reader.Run.Revision || before.Reader.Security.Tenant.Revision != after.Reader.Security.Tenant.Revision
            || before.Reader.Agent.Instance.Revision != after.Reader.Agent.Instance.Revision) throw new SecurityConflictException("memory_source_changed");
        return Serialize(after, remainingBytes);
    }
    public async ValueTask<TenantMemoryOperationObservation> InspectionAsync(TenantApprovalRequest request, Guid human, CancellationToken ct = default)
    {
        var value = request.Operation.MemoryOperation ?? throw new AccessDeniedException("approval_unavailable");
        var args = TenantMemoryToolContract.Parse(value.ToolName, value.ArgumentsJson);
        return await memories.ObserveTenantMemoryOperationAsync(new(request.TenantId, request.RunId), null, args, value, human, ct).ConfigureAwait(false);
    }
    public ValueTask ReviewerAsync(TenantApprovalRequest request, TenantMemoryOperationObservation observation, Guid human, CancellationToken ct = default)
        => TenantMemoryOperationReview.ReviewerAsync(request, observation, human, iam, ct);
    public async ValueTask<bool> IndependentAsync(TenantApprovalRequest request, TenantMemoryOperationObservation observation, CancellationToken ct = default)
    {
        foreach (var (key, scope) in TenantMemoryOperationRules.Targets(observation.Reader, observation.Items, !observation.Arguments.ReadOnly))
            if (TenantOperationPolicyService.Effective(observation.Reader.Security, PermissionCatalog.Get(key)).IndependentReview
                && (key == request.Permission && scope == TenantApprovalRules.Scope(request.TenantId, request.Operation)
                    || await TenantSandboxReadOperationRules.NeedsReviewAsync(observation.Reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public async ValueTask<string> InspectApprovedAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, int budget, CancellationToken ct = default)
    {
        if (receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId || receipt.RunId != frame.RunId
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ActionHash != request.Operation.ActionHash || request.Operation.MemoryOperation is not { } memory
            || !TenantMemoryToolContract.ReadOnly(memory.ToolName)) throw new AccessDeniedException("memory_read_required");
        var observed = await PrepareAsync(new(frame.Owner.TenantId!.Value, frame.RunId), frame.Generation, memory.ToolName, memory.ArgumentsJson, ct, memory).ConfigureAwait(false);
        // The owning source guard checks the completed same-task read receipt and its current
        // reviewer; a completed consent is never permission to substitute another value.
        foreach (var item in observed.Items)
            await sources.RequireTenantMemorySourcesAsync(new(request.TenantId, frame.RunId),
                [new(item.TenantId, item.MemoryId, item.ContentHash, item.Status == MemoryItemStatus.Deleted)], frame.Generation, ct).ConfigureAwait(false);
        return Serialize(observed, budget, receipt.OperationId);
    }
    public static string Serialize(TenantMemoryOperationObservation observation, int remainingBytes, Guid? operationId = null)
    {
        var budget = Math.Min(6000, remainingBytes); var items = observation.Items.Take(observation.Arguments.Limit).ToList();
        if (observation.Arguments.Tool == ToolCatalog.ContinuityGet) items = observation.Items.ToList();
        while (true)
        {
            var more = observation.HasMore || items.Count < observation.Items.Count; var payload = new JsonObject {
                ["items"] = JsonSerializer.SerializeToNode(items.Select(i => new { kind = "Memory", id = i.MemoryId, memoryKind = i.Kind.ToString(),
                    subject = i.Subject, content = i.Content, status = i.Status.ToString(), provenance = new { scope = TenantMemoryRules.ScopeName(i.Scope), i.Source,
                        sourceMemoryIds = TenantMemoryOperationMutation.Sources(i) } })),
                ["hasMore"] = more, ["tenantMemoryRead"] = true,
                ["memorySources"] = JsonSerializer.SerializeToNode(observation.Items.Take(items.Count + (more ? 1 : 0)).Select((i, index) => new TenantMemorySource(i.TenantId, i.MemoryId, i.ContentHash, index >= items.Count || i.Status == MemoryItemStatus.Deleted))),
                ["operationId"] = operationId?.ToString("D") };
            var text = payload.ToJsonString(); if (Encoding.UTF8.GetByteCount(text) <= budget) return text;
            if (items.Count <= 1) return """{"error":"output_limit","finish_required":true}""";
            items.RemoveAt(items.Count - 1);
        }
    }
}
