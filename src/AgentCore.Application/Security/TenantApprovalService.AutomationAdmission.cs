using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    private async ValueTask<TenantApprovalRequest> ChangeAutomationAdmissionAsync(TenantSecurityState state, TenantApprovalRequest request,
        TenantSessionInitiator human, TenantApprovalStatus outcome, CancellationToken ct)
    {
        if (outcome != TenantApprovalStatus.Cancelled)
        {
            var current = await (automationAdmissions ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(request, ct).ConfigureAwait(false);
            if (current.State.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
            await TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
        }
        var at = time.GetUtcNow(); var next = request.Decide(outcome, request.Revision, at);
        var decision = new TenantApprovalDecision(ids.NewId(), request.RequestId, request.TenantId, human.PrincipalId, outcome, request.Operation.ActionHash, next.Revision, at);
        var grant = outcome == TenantApprovalStatus.Approved && request.Kind == ApprovalKind.ExceptionalGrant
            ? new TenantOperationGrant(ids.NewId(), request.RequestId, request.TenantId, request.ExecutorPrincipalId, request.Operation.OperationId, request.Operation.ActionHash,
                "operation:" + request.Operation.OperationId.ToString("D"), OperationGrantStatus.Available, 1, request.ExpiresAt) : null;
        var signal = new ApprovalProcessedSignal(ids.NewId(), request.TenantId, request.RequestId, request.RunId, next.Revision, 1, at, null);
        var audit = new AuthorizationAudit(ids.NewId(), request.TenantId, human.PrincipalId, "approvals.decide", request.RequestId.ToString("D"), "allowed",
            outcome switch { TenantApprovalStatus.Approved => "approval_approved", TenantApprovalStatus.Rejected => "approval_rejected", _ => "approval_cancelled" }, at);
        if (!await approvals.TryDecideApprovalAsync(Next(state), state.Tenant.Revision, next, request.Revision, decision, grant, signal, human, audit, ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        return next;
    }

    private async ValueTask<bool> ProcessAutomationAdmissionAsync(ApprovalProcessedSignal signal, TenantApprovalRequest request, CancellationToken ct)
    {
        var store = automationAdmissions?.Store ?? throw new AccessDeniedException("tenant_effect_unavailable");
        var state = await security.ReadTenantAsync(request.TenantId, ct).ConfigureAwait(false); if (state is null) return false;
        TenantAutomationAdmission? admission = null; string? refusal = null;
        if (request.Status == TenantApprovalStatus.Approved)
        {
            try
            {
                var current = await (automationAdmissions ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(request, ct).ConfigureAwait(false);
                if (current.State.Tenant.Revision != state.Tenant.Revision) return false;
                var decision = await (effects ?? throw new AccessDeniedException("tenant_effect_unavailable")).FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("approval_unavailable");
                TenantAutomationAdmissionApprovalRules.Decision(request, decision, await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false), time.GetUtcNow());
                await TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, request, decision.HumanPrincipalId, authorizer, ct).ConfigureAwait(false);
                admission = TenantAutomationAdmissionApprovalRules.Build(request, current.Agent, current.Configuration, time.GetUtcNow(), current.Conversation);
            }
            catch (AccessDeniedException error) { refusal = error.ReasonCode; }
            catch (SecurityConflictException error) { refusal = error.ReasonCode; }
            catch (AgentCoreException) { refusal = "operation_configuration_changed"; }
        }
        return await store.TrySettleTenantAutomationApprovalAsync(state.Tenant.Revision, signal, admission, refusal, ids.NewId(), time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    private async ValueTask<TenantApprovalReview> ReviewAutomationAdmissionAsync(TenantApprovalView view, TenantSecurityState state, TenantSessionInitiator human, CancellationToken ct)
    {
        var r = view.Request; var p = r.Operation.AutomationAdmission!; var at = time.GetUtcNow();
        var target = await agents.FindTenantAgentAsync(r.TenantId, r.Operation.InstanceId, ct).ConfigureAwait(false);
        var occurrence = automationAdmissions?.Store is null ? null : await automationAdmissions!.Store.FindTenantOccurrenceAsync(r.TenantId, p.Occurrence.OccurrenceId, ct).ConfigureAwait(false);
        var cancel = r.Status == TenantApprovalStatus.Pending && at < r.ExpiresAt
            && (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("approvals.cancel"), AccessScope.ForResource(r.TenantId, "approval", r.RequestId.ToString("D")), ct).ConfigureAwait(false)).Allowed;
        string? unavailable = r.Status != TenantApprovalStatus.Pending ? "request_terminal" : at >= r.ExpiresAt ? "approval_expired" : null;
        if (unavailable is null)
        {
            try
            {
                var current = await (automationAdmissions ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(r, ct).ConfigureAwait(false);
                if (current.State.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
                await TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, r, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            catch (AccessDeniedException error) { unavailable = error.ReasonCode; }
            catch (SecurityConflictException error) when (error.ReasonCode != "stale_revision") { unavailable = error.ReasonCode; }
            catch (AgentCoreException) { unavailable = "operation_configuration_changed"; }
        }
        var independent = false;
        foreach (var (key, scope) in TenantAutomationAdmissionApprovalRules.Targets(r))
            independent |= TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview
                && (key == r.Permission || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, r.OriginalInitiatorPrincipalId, key, scope, authorizer, ct).ConfigureAwait(false));
        async ValueTask<string?> Name(Guid? principal)
        {
            var user = state.Users.SingleOrDefault(u => u.PrincipalId == principal);
            return user is null ? null : (await security.ReadAccountAsync(user.AccountId, ct).ConfigureAwait(false))?.DisplayName;
        }
        var execution = occurrence?.Disposition == OccurrenceRoutingDisposition.AcceptedDurable ? "admitted"
            : occurrence?.Disposition == OccurrenceRoutingDisposition.Rejected ? "notAdmitted" : r.Status == TenantApprovalStatus.Pending ? "waitingDecision" : "awaitingAdmission";
        var changes = new List<TenantApprovalFieldChange>
        {
            new("admissionAutomation", null, p.Current.Name, false), new("admissionSource", null,
                p.Occurrence.SourceKind switch { TriggerSourceKind.Schedule => "Scheduled occurrence · no human requester", TriggerSourceKind.CoreEvent => "Built-in event · no human requester", TriggerSourceKind.ApplicationEvent => "Webhook event · no human requester", _ => "Explicit manual invocation" }, false),
            new("admissionOccurrence", null, $"{(p.EventSource is { IsBucket: true } ? "Batch window ended" : p.EventSource is not null ? p.Occurrence.SourceKind == TriggerSourceKind.ApplicationEvent ? "Producer occurrence time (untrusted)" : "Source occurrence time" : p.Occurrence.ScheduledAtUtc is null ? "Invoked" : "Scheduled")} at {(p.Occurrence.ScheduledAtUtc ?? p.Occurrence.ObservedAtUtc).ToUniversalTime():yyyy-MM-dd HH:mm:ss.fff} UTC · prepared {r.CreatedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fff} UTC · saved registration revision {p.Current.Revision}", false),
            new("admissionPrimaryPermission", null, r.Permission, false), new("admissionObjective", null, p.Current.Instructions, false), new("admissionSchedule", null, AutomationRules.Describe(p.Current), false),
            new("admissionDestination", null, p.Current.ExecutionTarget.SessionId is { } targetSession ? $"Exact saved conversation {targetSession:D} · reply in that conversation; no separate report" : p.Occurrence.CompletionDelivery.SessionId is { } destination ? $"New separate Background Session · report requested to conversation {destination:D}; delivery requires separate current access and consent" : "New separate Background Session · no completion report", false), new("admissionModel", null, p.Model.CatalogKey, false),
            new("admissionScope", null, string.Join("; ", TenantAutomationAdmissionApprovalRules.Targets(r).Select(t => t.Permission).Distinct()), false),
            new("admissionLimit", null, "One exact reserved occurrence. No work is created before settlement. An exceptional grant covers only its named primary permission; all raw reads, other permissions and later tools remain independent.", false)
        };
        if (p.Conversation is { } conversation)
            changes.Add(new("admissionConversation", null, $"Conversation revision {conversation.SessionRevision} · input through entry sequence {conversation.InputBoundarySequence}", false));
        if (p.EventSource is { } source)
        {
            AgentCore.Domain.Events.EventSubscriptionSnapshot? snapshot = null;
            if (security is ITenantEventStore events)
                snapshot = source.IsBucket
                    ? (await events.FindTenantEventBucketAsync(r.TenantId, source.SourceId, ct).ConfigureAwait(false))?.Bucket.Subscription
                    : (await events.FindTenantEventDeliveryAsync(r.TenantId, source.SourceKind, source.SourceId, source.TriggerId, ct).ConfigureAwait(false))?.Subscription;
            if (snapshot is null) unavailable ??= "event_source_unavailable";
            using var evidence = System.Text.Json.JsonDocument.Parse(p.Occurrence.EvidenceJson);
            var context = evidence.RootElement.GetProperty("triggerContext");
            var data = context.GetProperty(source.IsBucket ? "sources" : "source");
            var identity = $"{(source.IsBucket ? "Coalesced batch" : "Receipt")} {source.SourceId:D} · child {source.TriggerId:D} · {source.EventIds.Count} source(s): {string.Join(", ", source.EventIds.Select(id => id.ToString("D")))}";
            if (source.ResourceId is { } resource) identity += $" · webhook {resource:D} at source revision {source.ResourceRevision}";
            changes.Add(new("admissionEventSource", null, identity, false));
            changes.Add(new("admissionEventFilter", null, snapshot?.FilterExpression ?? "No saved predicate · every eligible receipt", false, snapshot is not null));
            changes.Add(new("admissionEventDispatch", null, snapshot is null ? null : snapshot.Dispatch.Mode == EventDispatchMode.EveryMatch
                ? "Every matched receipt" : $"Coalesce latest · {snapshot.Dispatch.WindowSeconds} second window", false, snapshot is not null));
            changes.Add(new("admissionEventData", null, System.Text.Json.JsonSerializer.Serialize(data), false));
        }
        return new(r.Snapshot?.AgentName ?? target?.Instance.Persona.Name ?? "Unavailable agent", await Name(r.OriginalInitiatorPrincipalId), await Name(view.Decision?.HumanPrincipalId),
            changes,
            unavailable is null, cancel, unavailable, independent, at, execution);
    }
}
