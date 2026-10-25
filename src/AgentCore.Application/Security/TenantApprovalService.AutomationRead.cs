using System.Text.Json;
using AgentCore.Domain.Triggers;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public sealed partial class TenantApprovalService
{
    internal async ValueTask<TenantAutomationApprovalVisibility> AutomationReadVisibilityAsync(Guid tenant, TenantSessionInitiator human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var emailSources = new List<IReadOnlyList<Guid>>();
        foreach (var key in new[] { "credentials.read", "credentials.use", "email.read" })
            if (await VisibleIds(state, human.PrincipalId, key, "credential", ct).ConfigureAwait(false) is { } ids) emailSources.Add(ids);
        IReadOnlyList<Guid>? accounts = emailSources.Count == 0 ? null : emailSources.Skip(1).Aggregate(emailSources[0].AsEnumerable(), (ids, next) => ids.Intersect(next)).ToArray();
        var createRuns = (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("agent-runs.create"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed;
        var createSessions = (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("sessions.create"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed;
        return new(createRuns && createSessions, await VisibleIds(state, human.PrincipalId, "agents.read", "agent", ct).ConfigureAwait(false),
            await VisibleIds(state, human.PrincipalId, "automations.read", "automation", ct).ConfigureAwait(false), await VisibleIds(state, human.PrincipalId, "automations.run", "automation", ct).ConfigureAwait(false),
            await VisibleIds(state, human.PrincipalId, "webhooks.read", "webhook", ct).ConfigureAwait(false),
            await VisibleIds(state, human.PrincipalId, "core-events.read", "event", ct).ConfigureAwait(false), accounts, createRuns, await VisibleIds(state, human.PrincipalId, "sessions.read", "session", ct).ConfigureAwait(false), await VisibleIds(state, human.PrincipalId, "agents.chat", "agent", ct).ConfigureAwait(false), IncludeOccurrenceReservations: false);
    }

    private static IReadOnlyList<TenantApprovalFieldChange> AutomationReadObservationChanges(PreparedAutomationRead read)
    {
        using var document = JsonDocument.Parse(read.ObservationJson); var root = document.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        var changes = new List<TenantApprovalFieldChange> {
            new("Saved result window", null, $"{items.Length} of {root.GetProperty("total")} currently readable Automations · page {root.GetProperty("page").GetInt32() + 1}", false)
        };
        static string Text(JsonElement value, string key, string fallback = "None") => value.TryGetProperty(key, out var field) && field.ValueKind != JsonValueKind.Null ? field.ToString() : fallback;
        static string Details(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) return "None";
            return string.Join("\n", value.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).Select(p => {
                var content = p.Value.ToString();
                if (p.Name is "Kind" or "kind" && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var kind)) content = ((TriggerScheduleKind)kind).ToString();
                if (p.Name is "Mode" or "mode" && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var mode)) content = ((EventDispatchMode)mode).ToString();
                var label = System.Text.RegularExpressions.Regex.Replace(p.Name, "(?<!^)[A-Z]", " $0");
                return label + ": " + content;
            }));
        }
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index]; string Label(string label) => items.Length == 1 ? label : $"Automation {index + 1} · {label}";
            void Add(string label, string value) => changes.Add(new(Label(label), null, value, false));
            Add("Automation", $"{Text(item, "name")}\nID: {Text(item, "automationId")}\nRevision: {Text(item, "revision")} · {Text(item, "status")}");
            Add(item.GetProperty("instructionsExcerpt").GetBoolean() ? "Instructions excerpt" : "Instructions", Text(item, "instructions"));
            Add("Model and requirements", $"Model: {Text(item, "modelOverrideOfferingId", "Agent default")}\nReasoning: {Text(item, "modelOverrideReasoningEffort", "Agent default")}\nTools required: {(item.GetProperty("requiresTools").GetBoolean() ? "Yes" : "No")} · Vision required: {(item.GetProperty("requiresVision").GetBoolean() ? "Yes" : "No")}");
            var target = item.GetProperty("executionTarget"); var report = item.GetProperty("completionDelivery");
            Add("Destination", (target.GetProperty("kind").GetInt32() == 0 ? "Independent Background work" : "Saved conversation: " + Text(target, "sessionId"))
                + "\nCompletion report: " + Text(report, "sessionId"));
            Add("Origin", Text(item, "presetId") == "None" ? "Created directly" : $"Recipe: {Text(item, "presetId")} · version {Text(item, "presetVersion")}");
            Add("Activity", $"Occurrences: {Text(item, "occurrenceCount")}\nNext occurrence (UTC): {Text(item, "nextOccurrenceAtUtc")}");
            Add("Triggers", $"{Text(item, "triggerCount")} saved triggers" + (Text(item, "nextTriggerCursor") == "None" ? "" : $" · more details from cursor {Text(item, "nextTriggerCursor")}"));
            if (item.GetProperty("triggers").ValueKind == JsonValueKind.Array)
                foreach (var trigger in item.GetProperty("triggers").EnumerateArray())
                {
                    var source = trigger.GetProperty("source");
                    Add("Trigger " + Text(trigger, "triggerId"), $"{Text(trigger, "kind")} · revision {Text(trigger, "revision")} · {(trigger.GetProperty("enabled").GetBoolean() ? "Enabled" : "Disabled")}\n"
                        + "Schedule: " + Details(trigger.GetProperty("schedule")) + "\nEvent source: " + (source.ValueKind == JsonValueKind.Object ? Text(source, "key", Text(source, "eventId")) : "None")
                        + "\nFilter" + (trigger.GetProperty("filterExcerpt").GetBoolean() ? " excerpt" : "") + ": " + Text(trigger, "filterExpression")
                        + "\nDispatch: " + Details(trigger.GetProperty("dispatch")));
                }
        }
        if (root.GetProperty("coverage").ValueKind == JsonValueKind.Object)
        {
            var coverage = root.GetProperty("coverage"); var index = 0;
            foreach (var source in coverage.GetProperty("items").EnumerateArray())
                changes.Add(new($"Unreviewed event source {++index}", null, $"{Text(source, "key")}\nEvent: {Text(source, "eventId")}\nTrigger: {Text(source, "triggerId")}\nBucket: {Text(source, "bucketId")}\nReceived (UTC): {Text(source, "receivedAtUtc")}\nSource Run: {Text(source, "sourceRunId")}\nCoverage: {Text(source, "completionCode")}", false));
            if (Text(coverage, "nextCursor") != "None") changes.Add(new("More source metadata", null, "Continue from coverage cursor " + Text(coverage, "nextCursor"), false));
        }
        return changes;
    }

    public async ValueTask<TenantApprovalPreparationResult> PrepareAutomationReadAsync(TenantRunReference reference, ModelToolCall call,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            await operations.ResolveForDecisionAsync(reference, "automations.read", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            var service = (automationAdmissions ?? throw new AccessDeniedException("tenant_effect_unavailable")).Reads;
            var observed = await service.PrepareAsync(reference, generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false); var reader = observed.Reader;
            if (reader.Run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            if (TenantAutomationReadRules.Sources(observed.Value).Length == 0) throw new AccessDeniedException("no_readable_automations");
            var op = TenantApprovalRules.PrepareAutomationRead(ids.NewId(), reader.Run.AgentInstanceId, observed.Value, reader.Agent.Instance.Revision, reader.Agent.Instance.PersonaRevision);
            op = TenantApprovalRules.ForRun(op, reader.Run);
            var prior = await approvals.FindRootApprovalAsync(reference.TenantId, reader.Run.Admission.Activation.RootActivationId, reader.Agent.PrincipalId, op, ct).ConfigureAwait(false);
            if (prior is not null) return await DuplicateOutcome(reader.Security, reader.Run, prior, ct).ConfigureAwait(false);
            var decision = await service.DecisionAsync(observed, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.RequiresApproval)
                return await PreparationOutcome(reader.Security, reader.Run, op, decision.Decision, decision.ReasonCode, null, ct).ConfigureAwait(false);
            var at = time.GetUtcNow(); var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, reader.Run.AgentRunId, reader.Run.SessionId,
                reader.Run.Admission.ExecutionContext!.RootActivationId, reader.Agent.PrincipalId, reader.Run.Admission.ExecutionContext.OriginalInitiatorPrincipalId,
                ApprovalKind.Review, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(reader.Agent.Instance.Persona.Name));
            var qualified = false;
            foreach (var human in reader.Security.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantAutomationReadService.ReviewerAsync(request, observed, human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(reader.Security, reader.Run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, reader.Run, reader.Security, reader.Agent, op, request.Snapshot, true, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "automations.read", error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
}
