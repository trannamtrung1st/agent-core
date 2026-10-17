using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantApprovalRules
{
    public static PreparedTenantOperation Prepare(Guid operationId, Guid instance, AgentIdentity persona, long instanceRevision, long personaRevision)
    {
        AgentDefinitionValidator.ValidateIdentity(persona);
        if (operationId == Guid.Empty || instance == Guid.Empty || instanceRevision < 1 || personaRevision < 1) throw new ArgumentException("Invalid prepared persona operation.");
        var json = JsonSerializer.Serialize(new { operation = "agents.persona.update", permission = TenantApprovalRequest.Permission,
            resourceType = TenantApprovalRequest.ResourceType, resourceId = instance.ToString("D"), expectedInstanceRevision = instanceRevision,
            expectedPersonaRevision = personaRevision, persona = new { persona.Name, persona.Role, persona.Description, persona.Tone } });
        if (Encoding.UTF8.GetByteCount(json) > 32768) throw new ArgumentException("Prepared operation exceeds its content bound.");
        return new(operationId, instance, persona, instanceRevision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    public static PreparedTenantOperation PreparePreferences(Guid operationId, Guid instance, PreparedConversationPreferences preferences, long instanceRevision, long personaRevision)
    {
        var normalized = TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Patch(preferences));
        if (operationId == Guid.Empty || instance == Guid.Empty || instanceRevision < 1 || personaRevision < 1 || normalized != preferences)
            throw new ArgumentException("Invalid prepared preference operation.");
        var json = JsonSerializer.Serialize(new { operation = "agents.preferences.update", permission = TenantApprovalRequest.Permission,
            resourceType = TenantApprovalRequest.ResourceType, resourceId = instance.ToString("D"), expectedInstanceRevision = instanceRevision,
            expectedPersonaRevision = personaRevision, preferences = normalized });
        return new(operationId, instance, null, instanceRevision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), normalized);
    }

    public static PreparedTenantOperation Rebuild(PreparedTenantOperation op) => (op.Persona, op.Preferences) switch
    {
        ({ } persona, null) => Prepare(op.OperationId, op.InstanceId, persona, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        (null, { } preferences) => PreparePreferences(op.OperationId, op.InstanceId, preferences, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        _ => throw new ArgumentException("Exactly one supported operation payload is required.")
    };

    public static void Validate(TenantApprovalRequest request, AgentRun run, TenantAgent target, bool requireCurrentWitness = true)
    {
        var context = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        var op = request.Operation;
        if (request.RequestId == Guid.Empty || request.TenantId != context.TenantId || request.RunId != run.AgentRunId || request.SessionId != run.SessionId
            || request.RootActivationId != context.RootActivationId || request.ExecutorPrincipalId != context.AgentPrincipalId
            || request.OriginalInitiatorPrincipalId != context.OriginalInitiatorPrincipalId || op.InstanceId != run.AgentInstanceId
            || target.TenantId != request.TenantId || target.Instance.InstanceId != op.InstanceId || target.PrincipalId != context.AgentPrincipalId
            || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Status) || request.Revision < 1
            || request.CreatedAt.Offset != TimeSpan.Zero || request.ExpiresAt.Offset != TimeSpan.Zero
            || request.ExpiresAt <= request.CreatedAt || request.ExpiresAt - request.CreatedAt > TimeSpan.FromDays(1)
            || Rebuild(op) != op)
            throw new ArgumentException("Approval does not match its exact prepared operation and owning Run.");
        if (requireCurrentWitness && (target.Instance.Revision != op.ExpectedInstanceRevision || target.Instance.PersonaRevision != op.ExpectedPersonaRevision))
            throw new SecurityConflictException("operation_witness_changed");
        if (requireCurrentWitness && target.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("executor_unavailable");
    }

    public static string DuplicateReason(TenantApprovalRequest request) => request.Status switch
    { TenantApprovalStatus.Pending => "already_pending", TenantApprovalStatus.Rejected => "previously_rejected", TenantApprovalStatus.Expired => "previously_expired",
        TenantApprovalStatus.Cancelled => "previously_cancelled", _ => "already_approved" };

    public static void ValidateDecision(TenantApprovalRequest before, TenantApprovalRequest next, long expectedRevision,
        TenantApprovalDecision decision, TenantOperationGrant? grant, ApprovalProcessedSignal signal, TenantSessionInitiator human, AuthorizationAudit audit)
    {
        if (before.Decide(decision.Status, expectedRevision, decision.DecidedAt) != next || decision.DecisionId == Guid.Empty
            || decision.RequestId != before.RequestId || decision.TenantId != before.TenantId || decision.HumanPrincipalId != human.PrincipalId
            || decision.Status is not (TenantApprovalStatus.Approved or TenantApprovalStatus.Rejected or TenantApprovalStatus.Cancelled) || decision.ActionHash != before.Operation.ActionHash
            || decision.RequestRevision != next.Revision || signal.SignalId == Guid.Empty || signal.TenantId != before.TenantId
            || signal.RequestId != before.RequestId || signal.RunId != before.RunId || signal.DecisionRevision != next.Revision
            || signal.Revision != 1 || signal.ProcessedAt is not null || signal.CreatedAt != decision.DecidedAt
            || audit.ActorPrincipalId != human.PrincipalId || audit.TenantId != before.TenantId || audit.Action != "approvals.decide" || audit.Target != before.RequestId.ToString("D"))
            throw new ArgumentException("Decision, exact operation, human and continuation signal must agree.");
        var needsGrant = next.Status == TenantApprovalStatus.Approved && before.Kind == ApprovalKind.ExceptionalGrant;
        if (needsGrant != (grant is not null) || grant is not null && (grant.GrantId == Guid.Empty || grant.RequestId != before.RequestId
            || grant.TenantId != before.TenantId || grant.ExecutorPrincipalId != before.ExecutorPrincipalId || grant.OperationId != before.Operation.OperationId
            || grant.ActionHash != before.Operation.ActionHash || grant.EffectIdempotencyKey != "operation:" + before.Operation.OperationId.ToString("D")
            || grant.Status != OperationGrantStatus.Available || grant.Revision != 1 || grant.ExpiresAt != before.ExpiresAt))
            throw new ArgumentException("Only an approved exact exceptional operation may receive its bound grant.");
    }

    public static bool TryApprovalId(AccessScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "approval"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }

    public static TenantApprovalRequest Expire(TenantApprovalRequest request, long expectedRevision, ApprovalProcessedSignal signal, DateTimeOffset at)
    {
        if (signal.SignalId == Guid.Empty || signal.TenantId != request.TenantId || signal.RequestId != request.RequestId || signal.RunId != request.RunId
            || signal.DecisionRevision != checked(expectedRevision + 1) || signal.Revision != 1 || signal.ProcessedAt is not null || signal.CreatedAt != at)
            throw new ArgumentException("Expiry continuation must name the exact request and Run.");
        return request.Decide(TenantApprovalStatus.Expired, expectedRevision, at);
    }
}
