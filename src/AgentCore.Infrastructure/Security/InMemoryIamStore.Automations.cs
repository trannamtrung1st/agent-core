using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantAutomationStore
{
    public ValueTask<Automation?> FindTenantAutomationAsync(Guid tenant, Guid automation, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (automationBacking.AdmissionGate) return ValueTask.FromResult(automationBacking.ReadTenantAutomation(tenant, automation)); }
    public ValueTask<TriggerOccurrence?> FindTenantOccurrenceAsync(Guid tenant, Guid occurrence, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (automationBacking.AdmissionGate) return ValueTask.FromResult(automationBacking.ReadTenantOccurrence(tenant, occurrence)); }
    public ValueTask<TenantAutomationPage> ListTenantAutomationsAsync(Guid tenant, Guid instance, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default, AutomationStatus? status = null)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            var rows = automationBacking.TenantAutomations(tenant).Where(a => a.Owner.AgentInstanceId == instance && (status is null || a.Status == status) && (allowed is null || allowed.Contains(a.AutomationId)))
                .OrderBy(a => a.AutomationId.ToString("D"), StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new TenantAutomationPage(rows.Skip(page * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize));
        }
    }
    public ValueTask<IReadOnlyList<Automation>> ListDueTenantAutomationsAsync(DateTimeOffset at, int limit, CancellationToken ct = default, string? afterId = null)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (automationBacking.AdmissionGate)
            return ValueTask.FromResult<IReadOnlyList<Automation>>(automationBacking.TenantAutomations().Where(a => a.Status == AutomationStatus.Active && a.NextOccurrenceAtUtc <= at
                && _tenants.GetValueOrDefault(a.Owner.TenantId!.Value)?.Tenant.Status == IdentityStatus.Active)
                .Where(a => afterId is null || string.CompareOrdinal(a.AutomationId.ToString("D"), afterId) > 0)
                .OrderBy(a => a.AutomationId.ToString("D"), StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 100)).ToArray());
    }
    public ValueTask<IReadOnlyList<Automation>> ListFutureTenantAutomationsAsync(Guid tenant, Guid instance, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (automationBacking.AdmissionGate)
            return ValueTask.FromResult<IReadOnlyList<Automation>>(automationBacking.TenantAutomations(tenant).Where(a => a.Owner.AgentInstanceId == instance
                && a.Status is AutomationStatus.Active or AutomationStatus.SuspendedPolicy).OrderBy(a => a.AutomationId.ToString("D"), StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 256)).ToArray());
    }
    public ValueTask<bool> TrySaveTenantAutomationAsync(TenantSecurityState next, long expectedSecurityRevision, Automation proposed, long expectedAutomationRevision,
        long expectedInstanceRevision, HumanSessionReference human, string permission, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            RequireHumanSession(before.Tenant.TenantId, human); var current = automationBacking.ReadTenantAutomation(before.Tenant.TenantId, proposed.AutomationId);
            if ((current?.Revision ?? 0) != expectedAutomationRevision) return ValueTask.FromResult(false);
            var agent = FindTenantAgentAsync(before.Tenant.TenantId, proposed.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            if (agent.Instance.Revision != expectedInstanceRevision) return ValueTask.FromResult(false);
            TenantAutomationRules.Owner(proposed, agent);
            if (proposed.Status == AutomationStatus.Active && agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_unavailable"); TenantSessionRules.RequireExecutor(before, agent);
            var policy = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
            if (current?.Status is AutomationStatus.Completed or AutomationStatus.Expired && proposed.Status != AutomationStatus.Cancelled) throw new AccessDeniedException("automation_terminal");
            TenantAutomationPresetRules.ValidateNew(current, proposed);
            TenantAutomationAuthoring.ValidateTransition(current, proposed, expectedAutomationRevision, permission, audit.At); TenantAutomationRules.Configuration(proposed, policy, audit.At, current: current);
            RequireAutomationHuman(before, human.PrincipalId, agent, proposed.AutomationId, permission, current is null, ct);
            RequireAutomationContextAuthoring(before, agent, current, proposed, human.PrincipalId, ct);
            if (permission is "automations.create" or "automations.update") TenantAutomationRules.HumanSourcesAsync(before, human.PrincipalId, proposed, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            if (permission is "automations.create" or "automations.update")
                TenantCompletionDestinationRules.AuthoringAsync(before, human.PrincipalId, proposed,
                    proposed.CompletionDelivery.SessionId is { } destination ? sessionBacking.ReadTenantSession(before.Tenant.TenantId, destination) : null,
                    new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
            if (permission is "automations.create" or "automations.update")
                RequireAutomationConversation(before, proposed, human.PrincipalId, null, ct);
            if (proposed.ExecutionTarget.Workflow is { } workflow && permission is "automations.create" or "automations.update")
            {
                var version = workflowVersions.GetValueOrDefault((before.Tenant.TenantId, workflow.WorkflowId, workflow.Version)) ?? throw new AccessDeniedException("workflow_version_unavailable");
                if (workflowDefinitions.GetValueOrDefault((before.Tenant.TenantId, workflow.WorkflowId)) is not { Retired: false }) throw new AccessDeniedException("workflow_version_unavailable");
                AgentCore.Application.Workflows.WorkflowAutomationRules.DestinationAsync(before, proposed, version, human.PrincipalId, new(this), ct).GetAwaiter().GetResult();
            }
            ValidateAutomationTransaction(next, expectedSecurityRevision, proposed, human.PrincipalId, permission, audit);
            if (proposed.Status == AutomationStatus.Active && current?.Status != AutomationStatus.Active
                && automationBacking.TenantAutomations(before.Tenant.TenantId).Count(a => a.Owner == proposed.Owner && a.Status == AutomationStatus.Active) >= (policy.TriggerPolicy?.MaxActiveRegistrations ?? 0))
                throw new AccessDeniedException("automation_limit_reached");
            EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            automationBacking.SaveTenantAutomation(proposed); _tenants[before.Tenant.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TryAdvanceTenantAutomationAsync(TenantSecurityState next, long expectedSecurityRevision, Automation current, Automation advanced, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); ct.ThrowIfCancellationRequested();
        lock (_gate) lock (instances.CredentialGate) lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision
                || automationBacking.ReadTenantAutomation(before.Tenant.TenantId, current.AutomationId) is not { } saved || !TenantAutomationRules.Same(saved, current)) return ValueTask.FromResult(false);
            var agent = FindTenantAgentAsync(before.Tenant.TenantId, current.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            TenantAutomationRules.Owner(current, agent); TenantSessionRules.RequireExecutor(before, agent); TenantAutomationRules.TechnicalAdvance(current, advanced, audit.At);
            if (TenantAutomationContextRules.IsSuspension(advanced)) RequireAutomationContextUnavailable(before, agent, current, ct);
            ValidateAutomationTransaction(next, expectedSecurityRevision, current, agent.PrincipalId, "automations.schedule.advance", audit);
            EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            automationBacking.SaveTenantAutomation(advanced); _tenants[before.Tenant.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    private void RequireAutomationHuman(TenantSecurityState state, Guid human, TenantAgent agent, Guid id, string permission, bool create, CancellationToken ct)
        => TenantAutomationRules.HumanAsync(state, human, agent, id, permission, create, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
    private static void ValidateAutomationTransaction(TenantSecurityState next, long revision, Automation value, Guid actor, string action, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != value.Owner.TenantId || next.Tenant.Revision != revision + 1 || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != actor
            || audit.Action != action || audit.Target != value.AutomationId.ToString("D") || audit.Decision != "allowed") throw new ArgumentException("Invalid Automation transaction.");
    }
    public ValueTask<TenantAutomationAdmissionResult?> TryAdmitTenantAutomationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAutomationAdmission admission, HumanSessionReference? human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantAutomationAdmissionResult?>(null);
            if (human is not null) RequireHumanSession(before.Tenant.TenantId, human);
            var current = automationBacking.ReadTenantAutomation(before.Tenant.TenantId, admission.Current.AutomationId);
            if (automationBacking.ReadTenantOccurrence(before.Tenant.TenantId, admission.Occurrence.OccurrenceId) is { } prior)
                return ValueTask.FromResult<TenantAutomationAdmissionResult?>(TenantAutomationRules.Existing(prior));
            if (current is null || !TenantAutomationRules.Same(current, admission.Current)) return ValueTask.FromResult<TenantAutomationAdmissionResult?>(null);
            var agent = FindTenantAgentAsync(before.Tenant.TenantId, current.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            var policy = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
            TenantAutomationRules.Admission(admission, agent, human?.PrincipalId);
            TenantAutomationRules.AdmissionAuthority(before, current, agent, policy, new IamAuthorizer(this), ct, human?.PrincipalId, admission.Occurrence.SourceKind, admission.EventSource, TenantEventAdmissionRules.EmailAccounts(admission)).GetAwaiter().GetResult();
            if (human is not null) RequireAutomationHuman(before, human.PrincipalId, agent, current.AutomationId, "automations.run", false, ct);
            ValidateAutomationTransaction(next, expectedSecurityRevision, current, agent.PrincipalId, "automations.occurrence.admit", audit);
            if (automationBacking.TenantBusy(current.AutomationId)) return ValueTask.FromResult<TenantAutomationAdmissionResult?>(null);
            RequireAutomationConversationAdmission(before, admission, human?.PrincipalId, null, false, ct);
            RequireTenantEventAdmission(admission, null, false, ct);
            EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            StageTenantAutomationGraph(admission);
            automationBacking.CommitTenantOccurrence(admission.Next, admission.Occurrence); SettleTenantEventSource(admission.EventSource, admission.Occurrence.OccurrenceId, false); _tenants[before.Tenant.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantAutomationAdmissionResult?>(new(true, admission.Occurrence, admission.Run.SessionId, admission.Run.AgentRunId));
        }
    }
}
