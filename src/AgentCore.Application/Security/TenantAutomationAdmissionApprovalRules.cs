using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed record TenantAutomationAdmissionDecision(string Permission, ApprovalKind? Kind);

public static class TenantAutomationAdmissionApprovalRules
{
    public const string PendingReason = "admission_approval_pending";
    public static readonly string[] Permissions = ["automations.run", "sessions.create", "agent-runs.create"];
    public static (string Permission, AccessScope Scope)[] Targets(Guid tenant, Guid automation) => Permissions.Select(key =>
        (key, key == "automations.run" ? TenantAutomationRules.Scope(tenant, automation) : AccessScope.ForTenant(tenant))).ToArray();

    public static (string Permission, AccessScope Scope)[] Targets(Automation value) =>
        Targets(value.Owner.TenantId!.Value, value.AutomationId).Where(t => !TenantAutomationConversationRules.Existing(value) || t.Permission != "sessions.create")
            .Concat(TenantAutomationConversationRules.Targets(value)).ToArray();

    public static bool IsSourceRead(string permission) => permission is "sessions.read" or "agents.chat" or "webhooks.read" or "core-events.read" or "credentials.read" or "credentials.use" or "email.read";
    public static (string Permission, AccessScope Scope)[] SourceTargets(PreparedEventAdmissionSource? source, IReadOnlyList<Guid>? emailAccounts = null)
    {
        if (source is null) return [];
        var targets = source.SourceKind == TriggerSourceKind.ApplicationEvent
            ? new[] { ("webhooks.read", TenantWebhookRules.Scope(source.TenantId, source.ResourceId!.Value)) }
            : source.EventIds.Select(id => ("core-events.read", TenantEventAdmissionRules.Scope(source.TenantId, id))).ToArray();
        return targets.Concat((emailAccounts ?? []).Distinct().SelectMany(id => TenantEmailWatchRules.SourcePermissions.Select(key => (key, TenantCredentialRules.Scope(source.TenantId, id))))).ToArray();
    }
    public static (string Permission, AccessScope Scope)[] Targets(PreparedAutomationAdmission source) => Targets(source.Current).Concat(SourceTargets(source.EventSource, source.EmailSourceAccountIds)).ToArray();
    public static (string Permission, AccessScope Scope)[] Targets(TenantApprovalRequest request) => Targets(request.Operation.AutomationAdmission ?? throw new ArgumentException("An exact occurrence source is required."));

    public static async ValueTask<TenantAutomationAdmissionDecision> EvaluateAsync(TenantSecurityState state, Automation value, TenantAgent agent,
        AgentCore.Domain.Definitions.AgentDefinition definition, Guid? requester, IamAuthorizer iam, CancellationToken ct, TriggerSourceKind? sourceKind = null, PreparedEventAdmissionSource? eventSource = null, IReadOnlyList<Guid>? emailAccounts = null)
    {
        TenantAutomationRules.AdmissionSource(state, value, agent, definition, requester, sourceKind);
        var missing = new List<string>(); var reviews = new List<string>();
        foreach (var (key, scope) in Targets(value).Concat(SourceTargets(eventSource, emailAccounts)))
        {
            var access = await iam.CheckOwnedTenantAsync(state, agent.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false);
            if (!access.Allowed)
            {
                if (access.ReasonCode != "permission_denied") throw new AccessDeniedException(access.ReasonCode);
                if (IsSourceRead(key)) throw new AccessDeniedException(key is "sessions.read" or "agents.chat" ? "automation_conversation_permission_required" : "event_source_read_required");
                missing.Add(key);
            }
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false)) reviews.Add(key);
        }
        if (missing.Count > 1) throw new AccessDeniedException("automation_executor_required");
        return missing.Count == 1 ? new(missing[0], ApprovalKind.ExceptionalGrant)
            : reviews.Count > 0 ? new(reviews[0], ApprovalKind.Review) : new("agent-runs.create", null);
    }

    public static TriggerOccurrence Source(PreparedAutomationAdmission p)
    {
        var evidence = JsonNode.Parse(p.Occurrence.EvidenceJson)!.AsObject();
        evidence["admissionApproval"] = JsonSerializer.SerializeToNode(new { requestId = p.RequestId, sessionId = p.SessionId, runId = p.RunId,
            activationId = p.ActivationId, entryId = p.EntryId, responseId = p.ResponseId, requesterPrincipalId = p.ManualInitiatorPrincipalId });
        return p.Occurrence.WithAdmissionEvidence(evidence.ToJsonString());
    }
    public static TriggerOccurrence Pending(PreparedAutomationAdmission p) => Source(p).WithRouting(OccurrenceRoutingDisposition.AwaitingDurableWork,
        PendingReason, 1, p.Occurrence.AdmittedAtUtc, null, null);
    public static bool SameOccurrence(TriggerOccurrence a, TriggerOccurrence b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    public static TenantAutomationAdmission Build(TenantApprovalRequest request, TenantAgent agent, ResolvedAgentRunConfiguration resolved, DateTimeOffset? admittedAt = null, TenantSession? conversation = null)
    {
        var p = request.Operation.AutomationAdmission ?? throw new SecurityConflictException("operation_mismatch");
        Shape(request, agent);
        if (TenantAutomationRules.Hash(resolved) != p.ConfigurationHash) throw new SecurityConflictException("operation_witness_changed");
        var source = Source(p); var at = admittedAt ?? source.AdmittedAtUtc; var model = p.Occurrence.ModelPin ?? throw new ArgumentException("An exact occurrence model is required.");
        if (p.Model.OfferingId != model.OfferingId || p.Model.ConnectionId != model.ConnectionId || p.Model.ExternalModelId != model.ExternalModelId || p.Model.ReasoningEffort != model.ReasoningEffort) throw new ArgumentException("Model witness changed.");
        if (TenantAutomationConversationRules.Existing(p.Current))
        {
            var target = TenantAutomationConversationRules.Require(p.Current, conversation, p.Conversation
                ?? throw new ArgumentException("Existing conversation consent requires its exact snapshot witness."));
            var accepted = Pending(p).WithExecutionAcceptance(p.SessionId, p.RunId, 1, at);
            var run = TenantAutomationConversationRules.ForOccurrence(target, source, agent, resolved, p.ActivationId, p.RunId, p.ResponseId,
                model, at, p.ManualInitiatorPrincipalId);
            return new(p.Current, p.Next, accepted, target.Owner, target.Snapshot, run, request.Operation.ExpectedInstanceRevision,
                p.ConfigurationHash, p.EventSource, target.Snapshot.Revision);
        }
        var graph = BackgroundSessionAdmissionFactory.ForOccurrence(p.SessionId, p.EntryId, p.ActivationId, p.RunId, p.ResponseId, source,
            resolved.Configuration.Definition, agent.Instance.Persona, agent.Instance.PersonaRevision, model, resolved.Skills, at, p.Current.Name,
            ExecutionBudgetResolver.Resolve(resolved.Configuration.Definition, resolved.InstanceBudgets, false), resolved.Configuration, p.ManualInitiatorPrincipalId, AgentCore.Application.Models.ExecutionModelPolicy.Preference(resolved.Configuration.Definition, agent.Instance, p.Current));
        return new(p.Current, p.Next, Pending(p).WithExecutionAcceptance(p.SessionId, p.RunId, 1, at),
            new(p.TenantId, p.SessionId, agent.Instance.InstanceId, agent.PrincipalId, agent.PrincipalId, at), graph.Session, graph.Run,
            request.Operation.ExpectedInstanceRevision, p.ConfigurationHash, p.EventSource);
    }

    public static void Shape(TenantApprovalRequest request, TenantAgent agent, bool current = true)
    {
        var p = request.Operation.AutomationAdmission ?? throw new ArgumentException("Automation admission source required.");
        if (TenantApprovalRules.Rebuild(request.Operation) != request.Operation || request.TenantId != p.TenantId || request.RequestId != p.RequestId
            || request.RunId != p.RunId || request.SessionId != p.SessionId || request.RootActivationId != p.ActivationId
            || request.ExecutorPrincipalId != p.Current.Owner.ExecutorPrincipalId || request.OriginalInitiatorPrincipalId != p.ManualInitiatorPrincipalId
            || request.CreatedAt != p.Occurrence.AdmittedAtUtc || request.ExpiresAt <= request.CreatedAt || request.ExpiresAt - request.CreatedAt > TimeSpan.FromDays(1)
            || request.Revision < 1 || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Status))
            throw new ArgumentException("Approval does not match its reserved occurrence.");
        if (IsSourceRead(request.Permission) && request.Kind != ApprovalKind.Review) throw new ArgumentException("Source read policy cannot be replaced by a creation grant.");
        TenantAutomationRules.Owner(p.Current, agent);
        TenantApprovalSnapshotRules.Shape(request);
        if (current && (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision))
            throw new SecurityConflictException("operation_witness_changed");
        if (current) TenantApprovalSnapshotRules.RequireCurrent(request, agent);
    }

    public static async ValueTask ReadAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        var p = request.Operation.AutomationAdmission!;
        foreach (var (key, scope) in Targets(p).Concat(new[] {
            ("agents.read", AccessScope.ForResource(request.TenantId, "agent", request.Operation.InstanceId.ToString("D"))),
            ("automations.read", TenantAutomationRules.Scope(request.TenantId, p.Current.AutomationId)) }))
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("approval_unavailable");
    }
    public static async ValueTask ExecutorAsync(TenantSecurityState state, TenantApprovalRequest request, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(request))
            if ((request.Kind == ApprovalKind.Review || key != request.Permission)
                && !(await iam.CheckOwnedTenantAsync(state, request.ExecutorPrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("operation_authority_changed");
        if (request.OriginalInitiatorPrincipalId is { } human)
            foreach (var key in new[] { "agents.read", "automations.read", "automations.run" })
            {
                var scope = key == "agents.read" ? AccessScope.ForResource(request.TenantId, "agent", request.Operation.InstanceId.ToString("D"))
                    : TenantAutomationRules.Scope(request.TenantId, request.Operation.AutomationAdmission!.Current.AutomationId);
                if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("requester_unavailable");
            }
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        await ReadAsync(state, request, human, iam, ct).ConfigureAwait(false);
        foreach (var (key, scope) in Targets(request))
        {
            if (key != request.Permission && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview && human == request.OriginalInitiatorPrincipalId)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed
                || key == request.Permission && request.Kind == ApprovalKind.ExceptionalGrant && !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.grant_exception"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }

    public static void Decision(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, DateTimeOffset at)
    {
        if (request.Status != TenantApprovalStatus.Approved || decision.Status != request.Status || decision.RequestId != request.RequestId
            || decision.TenantId != request.TenantId || decision.ActionHash != request.Operation.ActionHash || decision.RequestRevision != request.Revision)
            throw new SecurityConflictException("operation_mismatch");
        if (at >= request.ExpiresAt) throw new AccessDeniedException("approval_expired");
        if (request.Kind == ApprovalKind.Review)
        { if (grant is not null) throw new SecurityConflictException("operation_mismatch"); return; }
        if (grant is null || grant.Status != OperationGrantStatus.Available || grant.RequestId != request.RequestId || grant.TenantId != request.TenantId
            || grant.OperationId != request.Operation.OperationId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId || grant.ActionHash != request.Operation.ActionHash
            || grant.ExpiresAt != request.ExpiresAt || grant.EffectIdempotencyKey != "operation:" + request.Operation.OperationId.ToString("D"))
            throw new AccessDeniedException("grant_unavailable");
    }
}
