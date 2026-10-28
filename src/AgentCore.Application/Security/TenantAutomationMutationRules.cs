using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public static class TenantAutomationMutationRules
{
    public const string Completed = "automation_mutation_completed";
    public static string ParametersHash(string tool, string arguments) => TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { tool, arguments }));
    public static string Permission(string tool) => tool == ToolCatalog.AutomationCreate ? "automations.create" : tool == ToolCatalog.AutomationDelete ? "automations.delete" : tool == ToolCatalog.AutomationRun ? "automations.run" : "automations.update";
    public static string? ConversationEvidence(TenantMemoryReader reader, IReadOnlyList<ConversationEntry> entries, long before)
    {
        if (reader.Root.Admission.ExecutionContext?.Origin != ExecutionOrigin.UserMessage || reader.User is null) return null;
        if (before <= 0) return null;
        // Same owning conversation only. Prior requests resolve references but grant no authority.
        // Assistant content is limited to what was actually received/heard.
        var evidence = entries.Where(e => e.Sequence < before && e.IsPromptTurn && e.Status != EntryStatus.Streaming).TakeLast(8)
            .Select(e => new { role = e.Role.ToString(), text = e.Role == ConversationRole.Assistant ? PromptContextBuilder.EligibleAssistantText(e) : e.Text })
            .Where(e => e.text.Length > 0).ToList();
        var json = JsonSerializer.Serialize(evidence);
        while (evidence.Count > 0 && json.Length > 12000) { evidence.RemoveAt(0); json = JsonSerializer.Serialize(evidence); }
        return json;
    }
    public static AccessScope Primary(Guid tenant, string tool, Automation value) => tool == ToolCatalog.AutomationCreate ? AccessScope.ForTenant(tenant) : TenantAutomationRules.Scope(tenant, value.AutomationId);
    public static void Eligible(TenantMemoryReader reader, string tool)
    {
        if (!TenantTaskExecutionContract.Allows(reader.Run, reader.Current) || !TenantAutomationMutationContract.Allows(reader.Run.Admission.Configuration!.Definition, tool)
            || !TenantAutomationMutationContract.Allows(reader.Current, tool) || reader.Agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active)
            throw new AccessDeniedException("automation_policy_unavailable");
        if (tool == ToolCatalog.AutomationRun && (reader.Root.Admission.ExecutionContext?.Origin != ExecutionOrigin.UserMessage || reader.User is null))
            throw new AccessDeniedException("automation_manual_request_required");
    }
    public static async ValueTask BaseRawAsync(TenantMemoryReader reader, Automation? current, IamAuthorizer iam, CancellationToken ct)
    {
        if (!(await iam.CheckOwnedTenantAsync(reader.Security, reader.Agent.PrincipalId, PermissionCatalog.Get("agents.read"), AccessScope.ForResource(reader.Security.Tenant.TenantId, "agent", reader.Run.AgentInstanceId.ToString("D")), ct).ConfigureAwait(false)).Allowed
            || current is not null && !(await iam.CheckOwnedTenantAsync(reader.Security, reader.Agent.PrincipalId, PermissionCatalog.Get("automations.read"), TenantAutomationRules.Scope(reader.Security.Tenant.TenantId, current.AutomationId), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("automation_source_read_required");
    }
    public static (string Text, bool Task) Intent(TenantMemoryReader reader, IReadOnlyList<ConversationEntry> entries)
    {
        string text; bool task;
        if (reader.Root.Admission.ExecutionContext?.Origin == ExecutionOrigin.UserMessage)
        {
            var ids = reader.Root.Admission.Activation.SourceEntryIds;
            if (reader.User is null || ids.Count == 0 || entries.Count != ids.Count || entries.Any(e => !ids.Contains(e.EntryId) || e.Role != ConversationRole.User))
                throw new AccessDeniedException("automation_intent_unavailable");
            text = string.Join("\n", entries.OrderBy(e => e.Sequence).Select(e => e.Text)); task = false;
        }
        else
        {
            if (reader.Root.Admission.TaskIntent is not { Text.Length: > 0 } intent) throw new AccessDeniedException("automation_intent_unavailable");
            text = intent.Text; task = true;
        }
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 16000) throw new AccessDeniedException("automation_intent_unavailable");
        return (text, task);
    }
    public static (Guid? Target, Guid? Report) Destinations(AgentRun run, Automation? current, string arguments)
    {
        if (current?.ExecutionTarget.Workflow is not null) throw new AccessDeniedException("automation_workflow_edit_in_designer");
        using var doc = JsonDocument.Parse(arguments); var root = doc.RootElement;
        var target = root.TryGetProperty("executionTarget", out var mode) ? mode.GetString() == "currentSession" ? run.SessionId : (Guid?)null : current?.ExecutionTarget.SessionId;
        var report = root.TryGetProperty("reportBack", out var back) ? back.GetBoolean() ? run.SessionId : (Guid?)null : target is not null ? null : current?.CompletionDelivery.SessionId;
        return (target, report);
    }
    public static string IntentHash(TenantAutomationMutationObservation source, TenantAutomationMutationArguments arguments) => TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new {
        source.Reader.Root.AgentRunId, source.Reader.Root.CreatedAtUtc, source.IntentText, source.AdmittedTask, arguments.Tool, arguments.Json,
        before = source.Current is null ? null : TenantAutomationReadRules.ConfigurationHash(source.Current)
    }));
    public static IEnumerable<(string Key, AccessScope Scope)> Targets(TenantAutomationMutationObservation source, PreparedAutomationMutation value)
    {
        var tenant = source.Reader.Security.Tenant.TenantId;
        yield return ("agents.read", AccessScope.ForResource(tenant, "agent", source.Reader.Run.AgentInstanceId.ToString("D")));
        if (source.Current is { } current) yield return ("automations.read", TenantAutomationRules.Scope(tenant, current.AutomationId));
        // Stopping future work never requires unrelated source permission; its exact
        // before/configuration and destination reads remain independently protected.
        if (value.ToolName is not (ToolCatalog.AutomationDelete or ToolCatalog.AutomationDisable or ToolCatalog.AutomationRun))
            foreach (var child in value.Proposed.Triggers)
                if (child.Configuration is EventTrigger webhook) yield return ("webhooks.read", TenantWebhookRules.Scope(tenant, webhook.EventId));
                else if (child.Configuration is CoreEventTrigger) yield return ("core-events.read", AccessScope.ForTenant(tenant));
        if (value.ToolName is ToolCatalog.AutomationDelete or ToolCatalog.AutomationDisable) yield break;
        if (value.ToolName == ToolCatalog.AutomationRun)
        {
            if (!TenantAutomationConversationRules.Existing(value.Current!)) yield return ("sessions.create", AccessScope.ForTenant(tenant));
            yield return ("agent-runs.create", AccessScope.ForTenant(tenant));
        }
        if (value.Proposed.ExecutionTarget.SessionId is { } targetId) { yield return ("sessions.read", TenantSessionRules.Scope(tenant, targetId)); yield return ("agents.chat", AccessScope.ForResource(tenant, "agent", source.Reader.Run.AgentInstanceId.ToString("D"))); }
        if (value.Proposed.CompletionDelivery.SessionId is { } report) { yield return ("sessions.read", TenantSessionRules.Scope(tenant, report)); yield return ("agents.chat", AccessScope.ForResource(tenant, "agent", source.Reader.Run.AgentInstanceId.ToString("D"))); }
    }
    public static async ValueTask RawAsync(TenantAutomationMutationObservation source, PreparedAutomationMutation value, IamAuthorizer iam, CancellationToken ct, Guid? human = null)
    {
        var targets = Targets(source, value);
        if (human is not null && value.Invocation is not null) targets = targets.Append(("automations.run", TenantAutomationRules.Scope(source.Reader.Security.Tenant.TenantId, value.Proposed.AutomationId)));
        foreach (var (key, scope) in targets.Distinct())
            if (!(await iam.CheckOwnedTenantAsync(source.Reader.Security, human ?? source.Reader.Agent.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("automation_source_read_required");
    }
    public static void Validate(TenantAutomationMutationObservation source, PreparedAutomationMutation value)
    {
        var args = TenantAutomationMutationContract.Parse(value.ToolName, value.ArgumentsJson); Eligible(source.Reader, value.ToolName);
        if (value.ToolName == ToolCatalog.AutomationRun) { TenantAutomationInvocationRules.Validate(source, value); return; }
        if (value.Invocation is not null) throw new SecurityConflictException("automation_plan_changed");
        if (value.SessionId != source.Reader.Run.SessionId || value.RootSessionId != source.Reader.Root.SessionId || args.Json != value.ArgumentsJson
            || value.IntentHash != IntentHash(source, args) || (source.Current is null) != (value.Current is null)
            || source.Current is not null && !TenantAutomationRules.Same(source.Current, value.Current!)) throw new SecurityConflictException("automation_source_changed");
        var change = TriggerScheduleCommands.NormalizeTenant(source.Reader.Current, source.Current, args, source.Reader.Run.SessionId, source.Reader.Root.CreatedAtUtc, new WitnessIds());
        var proposed = value.Proposed;
        if (value.ToolName != ToolCatalog.AutomationDelete)
        {
            if (change.Name != proposed.Name || change.Instructions != proposed.Instructions || change.Enabled != (proposed.Status == AutomationStatus.Active)
                || change.ModelPreference != proposed.ModelPreference
                || change.RequiresTools != proposed.RequiresTools || change.RequiresVision != proposed.RequiresVision
                || change.ReportToSessionId != proposed.CompletionDelivery.SessionId || change.TargetSessionId != proposed.ExecutionTarget.SessionId
                || proposed.ExpiresAtUtc != source.Current?.ExpiresAtUtc
                || (change.Schedule is null) != !proposed.IsSchedule
                || change.Schedule is { } schedule && !new ScheduleTrigger(TenantAutomationRules.CanonicalSchedule(schedule)).SemanticEquals(proposed.Trigger!))
                throw new SecurityConflictException("automation_plan_changed");
            if (change.Events is { } events && (events.Count != proposed.Triggers.Count || events.Zip(proposed.Triggers).Any(p =>
                p.First.TriggerId is { } id && id != p.Second.TriggerId || p.First.Enabled != p.Second.Enabled || !p.First.Configuration.SemanticEquals(p.Second.Configuration))))
                throw new SecurityConflictException("automation_plan_changed");
            if (source.Current is null && (proposed.Provenance.PresetId != change.PresetId || proposed.Provenance.PresetVersion != change.PresetVersion
                || proposed.Provenance.SourceSessionId is not null || proposed.Provenance.SourceEventId is not null)) throw new SecurityConflictException("automation_plan_changed");
        }
        TenantAutomationRules.Owner(value.Proposed, source.Reader.Agent);
        if (source.Current is not null && source.Current.AutomationId != value.Proposed.AutomationId || source.Current is null && value.Proposed.Provenance.AuthorizationOrigin != TriggerAuthorizationOrigin.AgentTask)
            throw new AccessDeniedException("automation_source_changed");
        TenantAutomationAuthoring.ValidateTransition(source.Current, value.Proposed, args.ExpectedRevision, Permission(args.Tool), value.Proposed.Provenance.UpdatedAt);
        TenantAutomationPresetRules.ValidateNew(source.Current, value.Proposed);
        TenantAutomationRules.Configuration(value.Proposed, source.Reader.Current, value.Proposed.Provenance.UpdatedAt, current: source.Current);
    }
    private sealed class WitnessIds : IIdGenerator
    {
        private int next;
        public Guid NewId() => new(++next, 0, 0, new byte[8]);
        public Guid NewSessionId() => throw new InvalidOperationException();
    }
    public static async ValueTask ReviewerAsync(TenantApprovalRequest request, TenantAutomationMutationObservation source, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        var value = request.Operation.AutomationMutation!; await RawAsync(source, value, iam, ct, human).ConfigureAwait(false);
        foreach (var (key, scope) in Targets(source, value).Append((request.Permission, TenantApprovalRules.Scope(request.TenantId, request.Operation))).Distinct())
        {
            if (key != request.Permission && !await TenantSandboxReadOperationRules.NeedsReviewAsync(source.Reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) continue;
            var policy = TenantOperationPolicyService.Effective(source.Reader.Security, PermissionCatalog.Get(key));
            if (policy.IndependentReview && human == request.OriginalInitiatorPrincipalId) throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(source.Reader.Security, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed
                || !(await iam.CheckOwnedTenantAsync(source.Reader.Security, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed
                || key == request.Permission && request.Kind == ApprovalKind.ExceptionalGrant && !(await iam.CheckOwnedTenantAsync(source.Reader.Security, human, PermissionCatalog.Get("approvals.grant_exception"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
}
