using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;
public static class TenantAutomationReadRules
{
    public const string Completed = "automation_read_completed";
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant,
        AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.AutomationRead is null || request.Kind != ApprovalKind.Review || grant is not null
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != Completed
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new SecurityConflictException("operation_mismatch");
    }
    public static void Eligible(TenantMemoryReader reader, string tool)
    {
        TenantSessionRules.RequireExecutor(reader.Security, reader.Agent);
        if (!TenantTaskExecutionContract.Allows(reader.Run, reader.Current)
            || !TenantAutomationReadContract.Allows(reader.Current, tool) || !TenantAutomationReadContract.Allows(reader.Run.Admission.Configuration!.Definition, tool))
            throw new AccessDeniedException("automation_read_unavailable");
    }
    public static TenantAutomationReadSource[] Sources(PreparedAutomationRead value) => JsonSerializer.Deserialize<TenantAutomationReadSource[]>(value.SourcesJson) ?? throw new ArgumentException("Use exact Automation sources.");
    public static TenantAutomationEventReadSource[] Events(PreparedAutomationRead value) => JsonSerializer.Deserialize<TenantAutomationEventReadSource[]>(value.EventSourcesJson) ?? throw new ArgumentException("Use exact event sources.");
    public static string ConfigurationHash(Automation value)
    {
        var json = JsonSerializer.Serialize(new { value.AutomationId, value.Owner, value.Revision,
        value.Name, value.Instructions, value.Status, value.ModelPreference, value.RequiresTools, value.RequiresVision,
        value.ExecutionTarget, value.CompletionDelivery, value.NextOccurrenceAtUtc, value.OccurrenceCount, value.ExpiresAtUtc, value.SuspensionReason, value.Provenance,
        triggers = value.Triggers.Select(t => new { t.TriggerId, t.Revision, t.Enabled, configuration = JsonSerializer.SerializeToElement(t.Configuration, t.Configuration.GetType()) }) });
        if (value.PinnedSpaceId is null) return TenantMemoryOperationRules.Hash(json);
        var context = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        context["PinnedSpaceId"] = value.PinnedSpaceId.Value;
        if (value.PinnedEnvironmentId is { } environment) context["PinnedEnvironmentId"] = environment;
        return TenantMemoryOperationRules.Hash(context.ToJsonString());
    }

    public static string EventHash(TenantEventDelivery delivery) => TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { delivery.TenantId, delivery.EventId,
        delivery.Subscription.Owner, delivery.Subscription.AutomationId, delivery.Subscription.TriggerId, delivery.Subscription.SourceKind, delivery.Subscription.ResourceId,
        delivery.Core, delivery.Webhook }));
    public static IReadOnlyList<(string Permission, AccessScope Scope)> Targets(TenantMemoryReader reader, PreparedAutomationRead value)
    {
        var tenant = reader.Security.Tenant.TenantId; var result = new List<(string, AccessScope)>();
        result.AddRange(Sources(value).Select(s => ("automations.read", TenantAutomationRules.Scope(tenant, s.AutomationId))));
        foreach (var source in Events(value))
        {
            result.Add(source.Kind == TriggerSourceKind.CoreEvent ? ("core-events.read", TenantEventAdmissionRules.Scope(tenant, source.EventId))
                : ("webhooks.read", TenantWebhookRules.Scope(tenant, source.ResourceId!.Value)));
            if (source.EmailAccountId is { } account) result.AddRange(TenantEmailWatchRules.SourcePermissions.Select(k => (k, TenantCredentialRules.Scope(tenant, account))));
        }
        return result.Distinct().ToArray();
    }
    public static async ValueTask RawAsync(TenantMemoryReader reader, PreparedAutomationRead value, IamAuthorizer iam, CancellationToken ct, Guid? human = null)
    {
        foreach (var (key, scope) in Targets(reader, value))
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, human ?? reader.Agent.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException(human is null ? "automation_read_required" : "approval_unavailable");
    }
    public static async ValueTask<IReadOnlyList<Guid>?> VisibleAsync(TenantMemoryReader reader, IamAuthorizer iam, CancellationToken ct)
    {
        var tenant = reader.Security.Tenant.TenantId; var principal = reader.Agent.PrincipalId;
        if ((await iam.CheckTenantAsync(reader.Security, principal, PermissionCatalog.Get("automations.read"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed) return null;
        var ids = new List<Guid>();
        foreach (var scope in reader.Security.Assignments.Where(a => a.PrincipalId == principal && reader.Security.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains("automations.read"))).Select(a => a.Scope).Distinct())
            if (TenantAutomationRules.TryId(scope, out var id) && (await iam.CheckOwnedTenantAsync(reader.Security, principal, PermissionCatalog.Get("automations.read"), scope, ct).ConfigureAwait(false)).Allowed) ids.Add(id);
        return ids;
    }
    public static async ValueTask<TenantEventReadVisibility> VisibilityAsync(TenantMemoryReader reader, IamAuthorizer iam, CancellationToken ct)
    {
        var tenant = reader.Security.Tenant.TenantId; var principal = reader.Agent.PrincipalId;
        return new(await Ids("core-events.read", "event"), await Ids("webhooks.read", "webhook"), await TenantEmailWatchRules.VisibleAccountsAsync(reader.Security, principal, iam, ct).ConfigureAwait(false));
        async ValueTask<IReadOnlyList<Guid>?> Ids(string permission, string type)
        {
            if ((await iam.CheckTenantAsync(reader.Security, principal, PermissionCatalog.Get(permission), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed) return null;
            var ids = new List<Guid>();
            foreach (var scope in reader.Security.Assignments.Where(a => a.PrincipalId == principal && a.Scope.ResourceType == type
                && reader.Security.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains(permission))).Select(a => a.Scope).Distinct())
                if (Guid.TryParseExact(scope.ResourceId, "D", out var id) && (await iam.CheckOwnedTenantAsync(reader.Security, principal, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed) ids.Add(id);
            return ids;
        }
    }
    public static PreparedAutomationRead Build(TenantMemoryReader reader, TenantAutomationReadArguments args, IReadOnlyList<Automation> parents, int total,
        EventCoveragePage? coverage, IReadOnlyList<TenantEventDelivery> deliveries)
    {
        var sources = JsonSerializer.Serialize(parents.Select(a => new TenantAutomationReadSource(a.AutomationId, a.Revision, ConfigurationHash(a))));
        var events = JsonSerializer.Serialize(deliveries.Select(d => new TenantAutomationEventReadSource(d.Subscription.AutomationId, d.EventId, d.Subscription.TriggerId,
            d.Subscription.SourceKind, d.Subscription.ResourceId, d.Core?.Event.Key == "email.message.received" ? AgentCore.Domain.Email.EmailEventReferences.AccountId(d.Core.Event.DataJson) : null, EventHash(d))));
        object Summary(Automation a) => new { a.AutomationId, a.Revision, a.Name, status = Status(a.Status), instructions = TenantContinuityRules.Safe(a.Instructions, args.AutomationId is null ? 160 : 600, out _),
            instructionsExcerpt = a.Instructions.Length > (args.AutomationId is null ? 160 : 600), a.ModelPreference, a.RequiresTools, a.RequiresVision,
            a.Provenance.PresetId, a.Provenance.PresetVersion, executionTarget = new { a.ExecutionTarget.Kind, a.ExecutionTarget.SessionId, workflow = a.ExecutionTarget.Workflow is { } workflow ? new { workflow.WorkflowId, workflow.Version, workflow.GraphHash } : null }, a.CompletionDelivery, a.NextOccurrenceAtUtc, a.OccurrenceCount,
            triggerCount = a.Triggers.Count, nextTriggerCursor = args.AutomationId is null || a.Triggers.Count <= args.TriggerCursor + 2 ? (int?)null : args.TriggerCursor + 2,
            triggers = args.AutomationId is null ? null : a.Triggers.Skip(args.TriggerCursor).Take(2).Select(t => new { t.TriggerId, t.Revision, t.Enabled, kind = t.Configuration.Kind.ToString(),
                schedule = t.Configuration is ScheduleTrigger s ? JsonSerializer.SerializeToElement(s.Schedule, s.Schedule.GetType()) : (JsonElement?)null,
                source = t.Configuration is CoreEventTrigger c ? (object)new { kind = "builtin", key = c.CoreEventKey } : t.Configuration is EventTrigger e ? new { kind = "webhook", eventId = e.EventId.ToString("D") } : null,
                filterExpression = t.Configuration is FilteredEventTrigger f ? TenantContinuityRules.Safe(f.FilterExpression ?? "", 256, out _) : null,
                filterExcerpt = t.Configuration is FilteredEventTrigger filter && filter.FilterExpression?.Length > 256,
                dispatch = (t.Configuration as FilteredEventTrigger)?.Dispatch }) };
        var observation = JsonSerializer.Serialize(new { items = parents.Select(Summary), total, args.Page, args.PageSize,
            coverage = coverage is null ? null : new { items = coverage.Items.Select(i => new { i.BucketId, i.TriggerId, i.CompletionCode, i.Source.EventId, i.Source.Key, i.Source.ReceivedAtUtc,
                sourceRunId = i.Source.Key is "run.completed" or "run.failed" && deliveries.Any(d => d.EventId == i.Source.EventId && d.Core is not null) ? RunId(i.Source.DataJson) : null }), coverage.NextCursor },
            boundedObservation = true, untrustedConfiguration = true }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (Encoding.UTF8.GetByteCount(observation) > 10000) throw new AccessDeniedException("automation_read_budget");
        return Bind(reader, args, sources, events, observation);
    }
    public static PreparedAutomationRead Bind(TenantMemoryReader reader, TenantAutomationReadArguments args, string sources, string events, string observation) =>
        new(reader.Run.SessionId, reader.Root.SessionId, args.Tool, args.Json, sources, events, observation, Hash(args.Tool, args.Json, sources, events, observation));
    public static string Hash(string tool, string args, string sources, string events, string observation) => TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { tool, args, sources, events, observation }));
    public static void Validate(PreparedAutomationRead value)
    {
        var args = TenantAutomationReadContract.Parse(value.ToolName, value.ArgumentsJson); var sources = Sources(value); var events = Events(value);
        if (args.Json != value.ArgumentsJson || value.SessionId == Guid.Empty || value.RootSessionId == Guid.Empty || sources.Length > 8 || events.Length > 4
            || sources.Any(s => s.AutomationId == Guid.Empty || s.Revision < 1 || !TenantMemoryOperationRules.ValidHash(s.ConfigurationHash)) || sources.Select(s => s.AutomationId).Distinct().Count() != sources.Length
            || events.Any(e => !sources.Any(s => s.AutomationId == e.AutomationId) || e.EventId == Guid.Empty || e.TriggerId == Guid.Empty || !TenantMemoryOperationRules.ValidHash(e.SourceHash)
                || e.Kind is not (TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent) || (e.Kind == TriggerSourceKind.ApplicationEvent) != (e.ResourceId is not null))
            || args.AutomationId is { } id && (sources.Length != 1 || sources[0].AutomationId != id)
            || Encoding.UTF8.GetByteCount(value.ObservationJson) > 10000 || Encoding.UTF8.GetByteCount(value.SourcesJson) > 4096 || Encoding.UTF8.GetByteCount(value.EventSourcesJson) > 4096
            || value.ObservationHash != Hash(value.ToolName, value.ArgumentsJson, value.SourcesJson, value.EventSourcesJson, value.ObservationJson)) throw new ArgumentException("Use an exact bounded owned Automation observation.");
        using var doc = JsonDocument.Parse(value.ObservationJson); if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use an Automation observation object.");
    }
    public static async ValueTask ValidateCurrentAsync(TenantMemoryReader reader, PreparedAutomationRead value,
        Func<Guid, CancellationToken, ValueTask<Automation?>> parentReader,
        Func<TriggerSourceKind, Guid, Guid, CancellationToken, ValueTask<TenantEventDelivery?>> eventReader,
        IamAuthorizer iam, CancellationToken ct, Guid? human = null, Func<TenantAutomationReadSource, Automation, CancellationToken, ValueTask<bool>>? ownMutation = null)
    {
        Validate(value); Eligible(reader, value.ToolName);
        if (value.SessionId != reader.Run.SessionId || value.RootSessionId != reader.Root.SessionId) throw new AccessDeniedException("automation_source_changed");
        foreach (var source in Sources(value))
        {
            var current = await parentReader(source.AutomationId, ct).ConfigureAwait(false);
            if (current is null || current.Owner != TriggerOwner.ForTenant(reader.Security.Tenant.TenantId, reader.Run.AgentInstanceId, reader.Agent.PrincipalId)
                || (current.Revision != source.Revision || ConfigurationHash(current) != source.ConfigurationHash)
                    && (ownMutation is null || !await ownMutation(source, current, ct).ConfigureAwait(false))) throw new AccessDeniedException("automation_source_changed");
        }
        foreach (var source in Events(value))
        {
            var current = await eventReader(source.Kind, source.EventId, source.TriggerId, ct).ConfigureAwait(false);
            if (current is null || current.Subscription.Owner != TriggerOwner.ForTenant(reader.Security.Tenant.TenantId, reader.Run.AgentInstanceId, reader.Agent.PrincipalId)
                || current.Subscription.AutomationId != source.AutomationId || current.Subscription.ResourceId != source.ResourceId || EventHash(current) != source.SourceHash
                || (current.Core?.Event.Key == "email.message.received" ? AgentCore.Domain.Email.EmailEventReferences.AccountId(current.Core.Event.DataJson) : (Guid?)null) != source.EmailAccountId)
                throw new AccessDeniedException("automation_source_changed");
        }
        await RawAsync(reader, value, iam, ct, human).ConfigureAwait(false);
    }
    public static string Status(AutomationStatus status) => status == AutomationStatus.SuspendedPolicy ? "suspended" : status.ToString().ToLowerInvariant();
    private static Guid? RunId(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("agentRunId", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var run) ? run : null; }
}
