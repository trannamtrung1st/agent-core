using System.Text.Json;
using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Admin;
using AgentCore.Application.Experience;
using AgentCore.Application.Continuity;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;

namespace AgentCore.Api;

internal static class ContinuityEndpoints
{
    internal static void Map(RouteGroupBuilder admin)
    {
        AdminScheduleEndpoints.Map(admin);
        var group = admin.MapGroup("/agent-instances/{instanceId:guid}");
        group.MapGet("/continuity-maintenance", (Guid instanceId, ExperienceService service, IContinuityMaintenanceStore store,
            ContinuityMaintenancePolicy policy, CancellationToken ct) => Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct);
                return Cadence(await store.ReadAsync(instanceId, ct), policy);
            }));
        group.MapPut("/continuity-maintenance", (Guid instanceId, HttpRequest http,
            ExperienceService service, IContinuityMaintenanceStore store, ContinuityMaintenancePolicy policy,
            IIdGenerator ids, TimeProvider time, CancellationToken ct) => Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct);
                if (!http.HasJsonContentType()) throw AgentCoreErrors.Validation("Continuity maintenance settings must be JSON.");
                ContinuityMaintenanceConfigurationRequest request;
                try { request = await http.ReadFromJsonAsync<ContinuityMaintenanceConfigurationRequest>(ct)
                    ?? throw AgentCoreErrors.Validation("Continuity maintenance settings are required."); }
                catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
                { throw AgentCoreErrors.Validation("Continuity maintenance interval must be a whole number of seconds or null."); }
                policy.ValidateInterval(request.IntervalSeconds);
                return Cadence(await store.ConfigureAsync(instanceId, request.ExpectedRevision, request.IntervalSeconds, ct,
                    Audit(ids, time, instanceId, "configureContinuityCadence") with
                    { Revision = request.ExpectedRevision + 1, SummaryJson = JsonSerializer.Serialize(new { instanceId, operation = "configureContinuityCadence", intervalSeconds = request.IntervalSeconds }) }), policy);
            }));
        group.MapGet("/maintenance", (Guid instanceId, ExperienceService service, IExperienceStore store, CancellationToken ct) =>
            Respond(async () => { await service.RequireInstanceAsync(instanceId, ct); return await store.MaintenanceSettingsAsync(instanceId, ct); }));
        group.MapPut("/maintenance", (Guid instanceId, IdentityMaintenanceConfigurationRequest request,
            ExperienceService service, IExperienceStore store, IIdGenerator ids, TimeProvider time, CancellationToken ct) => Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct);
                return await store.ConfigureMaintenanceAsync(instanceId, request.ExpectedRevision, request.AllowAgentConsolidation, ct,
                    Audit(ids, time, instanceId, "configureConsolidation", request.AllowAgentConsolidation));
            }));
        group.MapGet("/experience", (Guid instanceId, ExperienceService service, IExperienceStore store, IWorkItemStore work, CancellationToken ct) =>
            Respond(async () => await ExperienceReview(instanceId, service, store, work, ct)));
        group.MapPut("/experience/configuration", (Guid instanceId, ExperienceConfigurationRequest request,
            ExperienceService service, IExperienceStore store, IWorkItemStore work, IAdminEventStore events, IIdGenerator ids, TimeProvider time, CancellationToken ct) =>
            Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct);
                await store.ConfigureAsync(instanceId, request.ExpectedRevision, request.Enabled, ct, Audit(ids, time, instanceId, "configure", request.Enabled));
                return await ExperienceReview(instanceId, service, store, work, ct);
            }));
        group.MapPost("/experience/checkpoints", (Guid instanceId, ExperienceCheckpointRequest request,
            ExperienceService service, IExperienceStore store, IWorkItemStore work, IAdminEventStore events, IIdGenerator ids, TimeProvider time, CancellationToken ct) =>
            Respond(async () =>
            {
                if (!Guid.TryParse(request.SessionId, out var sessionId)) throw AgentCoreErrors.Validation("Source Session is invalid.");
                var record = await service.RequestSessionAsync(instanceId, sessionId, ct);
                await events.AppendAsync(Audit(ids, time, instanceId, "checkpoint", recordId: record.ExperienceId), ct);
                return await ExperienceReview(instanceId, service, store, work, ct);
            }));
        group.MapPut("/experience/{experienceId:guid}", (Guid instanceId, Guid experienceId, ExperienceVisibilityRequest request,
            ExperienceService service, IExperienceStore store, IWorkItemStore work, IAdminEventStore events, IIdGenerator ids, TimeProvider time, CancellationToken ct) =>
            Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct);
                if (!Enum.GetNames<ExperienceVisibility>().Contains(request.Visibility, StringComparer.Ordinal)) throw AgentCoreErrors.Validation("Experience visibility is invalid.");
                await store.SetVisibilityAsync(instanceId, experienceId, request.ExpectedRevision, Enum.Parse<ExperienceVisibility>(request.Visibility), ct, Audit(ids, time, instanceId, request.Visibility, recordId: experienceId));
                return await ExperienceReview(instanceId, service, store, work, ct);
            }));
        group.MapPost("/experience/reset", (Guid instanceId, ExperienceService service, IExperienceStore store, IWorkItemStore work,
            IAdminEventStore events, IIdGenerator ids, TimeProvider time, CancellationToken ct) => Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct); await store.ResetAsync(instanceId, ct, Audit(ids, time, instanceId, "reset"));
                return await ExperienceReview(instanceId, service, store, work, ct);
            }));
        group.MapGet("/thoughts", (Guid instanceId, ExperienceService service, ITriggerStore triggers, IWorkItemStore work,
            IAgentDefinitionStore definitions, IModelCatalog catalog, CancellationToken ct) =>
            Respond(async () => await ThoughtReview(instanceId, service, triggers, work, definitions, catalog, ct)));
        group.MapPost("/thoughts", (Guid instanceId, ThoughtRegistrationRequest request, ThoughtRegistrationService service, CancellationToken ct) =>
            Respond(async () => Thought(await service.SaveAsync(instanceId, null, request.ExpectedRevision, request.Enabled,
                request.IntervalSeconds, request.ThinkingPrompt, request.ModelKey, request.ReasoningEffort, ct), null, null)));
        group.MapPut("/thoughts/{registrationId:guid}", (Guid instanceId, Guid registrationId, ThoughtRegistrationRequest request,
            ThoughtRegistrationService service, CancellationToken ct) => Respond(async () =>
            Thought(await service.SaveAsync(instanceId, registrationId, request.ExpectedRevision, request.Enabled,
                request.IntervalSeconds, request.ThinkingPrompt, request.ModelKey, request.ReasoningEffort, ct), null, null)));
        group.MapPost("/thoughts/{registrationId:guid}/delete", (Guid instanceId, Guid registrationId, ContinuityRevisionRequest request,
            ThoughtRegistrationService service, CancellationToken ct) => Respond(async () =>
            { await service.DeleteAsync(instanceId, registrationId, request.ExpectedRevision, ct); return new { deleted = true }; }));
        group.MapPost("/thoughts/{registrationId:guid}/run", (Guid instanceId, Guid registrationId, ContinuityRevisionRequest request,
            ThoughtRegistrationService service, CancellationToken ct) => Respond(async () =>
            { var o = await service.RunNowAsync(instanceId, registrationId, request.ExpectedRevision, ct); return new { occurrenceId = o.OccurrenceId }; }));
        MapWork(group);
    }

    private static ContinuityMaintenanceResponse Cadence(ContinuityMaintenanceSettings settings, ContinuityMaintenancePolicy policy) =>
        new(settings.IntervalSeconds, policy.Effective(settings.IntervalSeconds), policy.MinimumIntervalSeconds,
            policy.MaximumIntervalSeconds, policy.DefaultIntervalSeconds, settings.IntervalSeconds is null,
            policy.Allows(settings.IntervalSeconds), settings.Revision,
            settings.LastMaintenanceAtUtc is { } last ? HttpMapping.Format(last) : null);

    private static void MapWork(RouteGroupBuilder group)
    {
        group.MapGet("/work-items", (Guid instanceId, int? limit, Guid? before, bool? attentionOnly, ExperienceService service, IWorkItemStore store, CancellationToken ct) => Respond(async () =>
        {
            if (limit is < 1) throw AgentCoreErrors.Validation("limit must be at least 1.");
            await service.RequireInstanceAsync(instanceId, ct);
            return new WorkItemListResponse((await store.ListPageAsync(new(instanceId, LocalUserProfile.Id), limit ?? 100, before, attentionOnly ?? false, ct)).Select(WorkItemEndpoints.ToResponse).ToArray());
        }));
        group.MapGet("/work-items/{workItemId:guid}", (Guid instanceId, Guid workItemId, ExperienceService service,
            IWorkItemStore store, CancellationToken ct) => Respond(async () =>
                WorkItemEndpoints.ToResponse(await RequireWork(instanceId, workItemId, service, store, ct))));
        group.MapGet("/work-items/{workItemId:guid}/result", (Guid instanceId, Guid workItemId, ExperienceService service,
            IWorkItemStore store, CancellationToken ct) => Respond(async () =>
        {
            var item = await RequireWork(instanceId, workItemId, service, store, ct);
            if (item.Result is not { } result) throw AgentCoreErrors.NotFound("Work result was not found.");
            return new WorkItemResultResponse(workItemId.ToString("D"), item.Provenance.SourceKind == WorkSourceKind.ThoughtActivation ? ThoughtCompletion.Summary(result.Text) : result.Text,
                HttpMapping.Format(result.CompletedAtUtc), result.AttentionRequired);
        }));
        group.MapPost("/work-items/{workItemId:guid}/cancel", (Guid instanceId, Guid workItemId, CancelWorkItemRequest request,
            ExperienceService service, IWorkItemStore store, DurableReminderExecutor executor, TimeProvider time, CancellationToken ct) => Respond(async () =>
        {
            var item = await RequireWork(instanceId, workItemId, service, store, ct);
            return WorkItemEndpoints.ToResponse(await executor.RequestCancellationAsync(item.Owner, workItemId, request.ExpectedRevision, null, time.GetUtcNow(), ct));
        }));
        foreach (var action in new[] { "approve", "reject" })
        {
            var decision = action == "approve" ? WorkApprovalDecision.Approved : WorkApprovalDecision.Rejected;
            group.MapPost("/work-items/{workItemId:guid}/approvals/{approvalId:guid}/" + action, (Guid instanceId, Guid workItemId,
                Guid approvalId, DecideWorkApprovalRequest request, ExperienceService service, IWorkItemStore store, TimeProvider time, CancellationToken ct) => Respond(async () =>
            {
                var item = await RequireWork(instanceId, workItemId, service, store, ct);
                return WorkItemEndpoints.ToResponse(await store.DecideApprovalAsync(item.Owner, workItemId, approvalId,
                    request.ExpectedRevision, request.ExpectedApprovalRevision, request.ActionHash, decision, time.GetUtcNow(), ct));
            }));
        }
    }
    private static async ValueTask<WorkItem> RequireWork(Guid instanceId, Guid workId, ExperienceService service, IWorkItemStore store, CancellationToken ct)
    {
        await service.RequireInstanceAsync(instanceId, ct);
        return await store.GetAsync(new(instanceId, LocalUserProfile.Id), workId, ct) ?? throw AgentCoreErrors.NotFound("Work item was not found.");
    }
    private static async Task<ExperienceReviewResponse> ExperienceReview(Guid instanceId, ExperienceService service, IExperienceStore store, IWorkItemStore work, CancellationToken ct)
    {
        await service.RequireInstanceAsync(instanceId, ct);
        var settings = await store.SettingsAsync(instanceId, ct);
        var rows = new List<ExperienceResponse>();
        foreach (var r in await store.ListAsync(instanceId, 50, ct))
        {
            if (r.Visibility == ExperienceVisibility.Deleted) continue;
            var w = await work.GetAsync(new(instanceId, LocalUserProfile.Id), r.GenerationWorkItemId, ct);
            rows.Add(new(r.ExperienceId.ToString("D"), r.SourceKind.ToString(), r.SourceId.ToString("D"), r.ThroughCursor,
                HttpMapping.Format(r.SourceAtUtc), r.DefinitionId, r.DefinitionVersion, r.GenerationWorkItemId.ToString("D"), r.Model.CatalogKey,
                r.Content is not null ? "Completed" : w?.Status.ToString() ?? "Pending", r.Visibility.ToString(), r.Revision,
                settings.Enabled && r.Content is not null && r.Visibility == ExperienceVisibility.Eligible,
                r.Content is { } c ? new(c.Goal, c.Attempts, c.Decisions, c.Outcomes, c.Corrections, c.Unresolved, c.Difficulties, c.Lessons) : null,
                w?.Failure?.DiagnosticId?.ToString("D"), w?.Failure?.Summary, HttpMapping.Format(r.SourceAtUtc),
                r.CheckpointAtUtc is { } checkpointAt ? HttpMapping.Format(checkpointAt) : null,
                (r.DerivedFromExperienceIds ?? []).Select(id => id.ToString("D")).ToArray(), r.MaintenanceOrigin));
        }
        return new(settings.Enabled, settings.Revision, ExperienceService.MaxContextCharacters, rows);
    }
    private static async Task<ThoughtReviewResponse> ThoughtReview(Guid instanceId, ExperienceService service, ITriggerStore triggers,
        IWorkItemStore work, IAgentDefinitionStore definitions, IModelCatalog catalog, CancellationToken ct)
    {
        var instance = await service.RequireInstanceAsync(instanceId, ct);
        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct);
        var registrations = await triggers.ListAsync(new(instanceId, LocalUserProfile.Id), null, ct);
        var items = new List<ThoughtRegistrationResponse>();
        foreach (var registration in registrations.Where(r => r.Provenance.AuthorizationOrigin == TriggerAuthorizationOrigin.AdminThought && r.Status != TriggerRegistrationStatus.Cancelled))
        {
            var last = await work.GetLatestForRegistrationAsync(new(instanceId, LocalUserProfile.Id), registration.RegistrationId, ct);
            items.Add(Thought(registration, last, definition is null ? null : ExecutionModelPolicy.Resolve(catalog, definition, instance, registration).Pin?.CatalogKey));
        }
        return new(ThoughtIntent.MinIntervalSeconds, items);
    }

    private static ThoughtRegistrationResponse Thought(TriggerRegistration r, WorkItem? w, string? effectiveModel) => new(r.RegistrationId.ToString("D"), r.Revision,
        r.Status == TriggerRegistrationStatus.Active, r.Status.ToString(), ((FixedIntervalSchedule)r.Schedule).IntervalSeconds, r.Intent, r.ModelOverrideCatalogKey,
        r.ModelOverrideReasoningEffort, r.NextOccurrenceAtUtc is { } next ? HttpMapping.Format(next) : null,
        w is null ? null : HttpMapping.Format(w.CreatedAtUtc), w?.Result is { } result ? ThoughtCompletion.Outcome(result.Text)
            : w?.Status == WorkItemStatus.WaitingForApproval ? "ApprovalPending" : w?.Status == WorkItemStatus.Failed ? "Failed" : null,
        w?.WorkItemId.ToString("D"), w?.Status.ToString(), effectiveModel);
    private static AdminEventAppend Audit(IIdGenerator ids, TimeProvider time, Guid instanceId, string operation,
        bool? enabled = null, Guid? recordId = null) => new(ids.NewId(), time.GetUtcNow(), AdminEventActorKind.LocalOwner,
            AdminEventOperationKind.ExperienceChanged, "agentInstance", instanceId.ToString("D"), null, null,
            JsonSerializer.Serialize(new { instanceId, operation, enabled, recordId }));
    private static async Task<IResult> Respond<T>(Func<Task<T>> action)
    {
        try { return Results.Json(await action()); }
        catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        catch (ArgumentException) { return ProblemResults.From(AgentCoreErrors.Validation("Continuity request is invalid.")); }
    }
}
