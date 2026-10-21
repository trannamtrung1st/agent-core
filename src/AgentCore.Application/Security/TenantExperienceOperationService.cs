using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public sealed class TenantExperienceOperationService(ITenantExperienceStore storage, TenantOperationAuthorizer operations, IamAuthorizer iam,
    ITenantApprovalStore approvals, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantExperienceOperationObservation> PrepareAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        CancellationToken ct = default, PreparedExperienceOperation? expected = null)
    {
        var result = await storage.ObserveTenantExperienceOperationAsync(reference, generation, TenantExperienceToolContract.Parse(tool, arguments), expected, ct: ct).ConfigureAwait(false);
        if (generation is not null && result.Reader.Run.Claim?.LeaseExpiresAtUtc <= time.GetUtcNow()) throw new SecurityConflictException("execution_claim_expired");
        return result;
    }
    public async ValueTask<OperationAuthorizationDecision> DecisionAsync(TenantExperienceOperationObservation observed, CancellationToken ct = default)
    {
        var reader = observed.Reader; var prepared = TenantApprovalRules.PrepareExperience(ids.NewId(), reader.Run.AgentInstanceId, observed.Value, reader.Agent.Instance.Revision, reader.Agent.Instance.PersonaRevision);
        var primary = TenantApprovalRules.Scope(reader.Security.Tenant.TenantId, prepared); var review = observed.Arguments.Tool == TenantExperienceToolContract.Consolidate;
        foreach (var (key, scope) in TenantExperienceConsentRules.Targets(observed).Distinct())
        {
            if (key == prepared.Permission && scope == primary) continue;
            var decision = await operations.Check(reader.Security, reader.Run, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.Denied || decision.ApprovalKind == ApprovalKind.ExceptionalGrant)
                return new(OperationDecisionKind.Denied, "experience_source_authority_required", prepared.Permission, primary);
            // Record's exact source guard already qualifies every raw/read Review target,
            // through ordinary current authority or the prior same-task source receipt.
            if (observed.Arguments.Tool != TenantExperienceToolContract.Record) review |= decision.Decision == OperationDecisionKind.RequiresApproval;
        }
        var result = await operations.Check(reader.Security, reader.Run, prepared.Permission, primary, ct, review).ConfigureAwait(false);
        if (observed.Arguments.ReadOnly && result.ApprovalKind == ApprovalKind.ExceptionalGrant) return result with { Decision = OperationDecisionKind.Denied, ReasonCode = "experience_read_required", ApprovalKind = null };
        if (observed.Arguments.ReadOnly && result.Decision == OperationDecisionKind.RequiresApproval)
        {
            try { await storage.RequireTenantExperienceSourcesAsync(new(reader.Security.Tenant.TenantId, reader.Run.AgentRunId), [new(reader.Security.Tenant.TenantId, observed.Source!.Witness)], [], reader.Run.Claim?.Generation, ct).ConfigureAwait(false);
                return result with { Decision = OperationDecisionKind.Allowed, ReasonCode = "exact_read_consent", ApprovalKind = null }; }
            catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return result;
    }
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 200) throw new ArgumentException("Use a trusted call identity.");
        var args = TenantExperienceToolContract.Parse(call.Name, call.ArgumentsJson); var reference = new TenantRunReference(frame.Owner.TenantId!.Value, frame.RunId);
        var hash = TenantMemoryOperationRules.Hash(args.CanonicalArguments);
        if (!args.ReadOnly && await storage.FindDirectOperationReceiptAsync(reference.TenantId, reference.RunId, call.Id, ct).ConfigureAwait(false) is { } saved)
        {
            var (run, _) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
            return Result(TenantLocalEffectRules.MatchDirectReceipt(saved, run, call.Id, hash), null, true);
        }
        var before = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
        var decision = await DecisionAsync(before, ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed || call.Name == TenantExperienceToolContract.Consolidate)
            return JsonSerializer.Serialize(new { decision = decision.Decision == OperationDecisionKind.Denied ? "denied" : "requiresApproval", reasonCode = decision.ReasonCode, applied = false });
        if (args.ReadOnly)
        {
            var after = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct, before.Value).ConfigureAwait(false);
            if (after.Reader.Security.Tenant.Revision != before.Reader.Security.Tenant.Revision || after.Reader.Run.Revision != before.Reader.Run.Revision
                || after.Reader.Agent.Instance.Revision != before.Reader.Agent.Instance.Revision) throw new SecurityConflictException("experience_source_changed");
            return Serialize(after.Source!, budget);
        }
        var target = before.Reader.Agent; var prepared = TenantApprovalRules.PrepareExperience(ids.NewId(), before.Reader.Run.AgentInstanceId, before.Value, target.Instance.Revision, target.Instance.PersonaRevision);
        var prior = await approvals.FindRootApprovalAsync(reference.TenantId, before.Reader.Run.Admission.Activation.RootActivationId, target.PrincipalId, prepared, ct).ConfigureAwait(false);
        if (prior is not null) return JsonSerializer.Serialize(new { decision = prior.Status == TenantApprovalStatus.Pending ? "requiresApproval" : "denied", reasonCode = TenantApprovalRules.DuplicateReason(prior), applied = false });
        var state = before.Reader.Security;
        var commit = new TenantExperienceCommit(prepared.OperationId, reference.RunId, frame.Generation, before.Reader.Run.Revision, call.Id, hash, before.Value, target.Instance.Revision, prepared.ActionHash);
        var receipt = await storage.TryCommitTenantExperienceAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }, state.Tenant.Revision, commit,
            new(ids.NewId(), state.Tenant.TenantId, target.PrincipalId, "operations.experience.record", target.Instance.InstanceId.ToString("D"), "allowed", TenantExperienceRules.Recorded, time.GetUtcNow()), ct).ConfigureAwait(false);
        if (receipt is null) throw new SecurityConflictException("stale_revision"); return Result(receipt, before.Value.ResultExperienceId, before.ExistingResult is not null);
    }
    private static string Result(TenantOperationReceipt receipt, Guid? id, bool existing) => JsonSerializer.Serialize(new { decision = "allowed", applied = !existing,
        alreadyCompleted = existing, experienceId = id, operationId = receipt.OperationId, reasonCode = receipt.ReasonCode });
    public static string Serialize(TenantExperienceSourceObservation observed, int budget, Guid? operation = null)
    {
        var body = new JsonObject { ["tenantExperienceSourceRead"] = true, ["sourceWitness"] = JsonSerializer.SerializeToNode(new TenantExperienceSourceReference(observed.Reader.Security.Tenant.TenantId, observed.Witness)),
            ["sourceKind"] = observed.Witness.Kind.ToString(), ["sourceId"] = observed.Witness.SourceId.ToString("D"), ["throughCursor"] = observed.Witness.ThroughCursor,
            ["evidence"] = JsonNode.Parse(observed.EvidenceJson), ["boundedObservation"] = true, ["untrustedHistory"] = true, ["operationId"] = operation?.ToString("D") };
        var json = body.ToJsonString(); return Encoding.UTF8.GetByteCount(json) <= Math.Min(6000, budget) ? json : AgentCore.Application.Execution.AgentRunToolCallCheckpoint.FinishRequiredResult(budget);
    }
    public ValueTask<TenantExperienceOperationObservation> InspectionAsync(TenantApprovalRequest request, Guid human, CancellationToken ct = default) =>
        storage.ObserveTenantExperienceOperationAsync(new(request.TenantId, request.RunId), null, TenantExperienceToolContract.Parse(request.Operation.ExperienceOperation!.ToolName, request.Operation.ExperienceOperation.ArgumentsJson), request.Operation.ExperienceOperation, human, ct);
    public ValueTask ReviewerAsync(TenantApprovalRequest request, TenantExperienceOperationObservation observed, Guid human, CancellationToken ct = default) => TenantExperienceConsentRules.ReviewerAsync(request, observed, human, iam, ct);
    public async ValueTask<bool> IndependentAsync(TenantApprovalRequest request, TenantExperienceOperationObservation observed, CancellationToken ct)
    {
        foreach (var (key, scope) in TenantExperienceConsentRules.Targets(observed).Distinct())
            if (TenantOperationPolicyService.Effective(observed.Reader.Security, PermissionCatalog.Get(key)).IndependentReview && (key == request.Permission && scope == TenantApprovalRules.Scope(request.TenantId, request.Operation)
                || await TenantSandboxReadOperationRules.NeedsReviewAsync(observed.Reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public async ValueTask<string> InspectApprovedAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, int budget, CancellationToken ct = default)
    {
        if (request.Operation.ExperienceOperation is not { ToolName: TenantExperienceToolContract.Source } value || receipt.Outcome != TenantOperationOutcome.Completed || receipt.RequestId != request.RequestId
            || receipt.OperationId != request.Operation.OperationId || receipt.RunId != frame.RunId || receipt.ActionHash != request.Operation.ActionHash) throw new AccessDeniedException("experience_read_review_required");
        var observed = await PrepareAsync(new(request.TenantId, frame.RunId), frame.Generation, value.ToolName, value.ArgumentsJson, ct, value).ConfigureAwait(false);
        await storage.RequireTenantExperienceSourcesAsync(new(request.TenantId, frame.RunId), [new(request.TenantId, observed.Source!.Witness)], [], frame.Generation, ct).ConfigureAwait(false);
        return Serialize(observed.Source, budget, receipt.OperationId);
    }
}
