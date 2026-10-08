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

namespace AgentCore.Api;

internal static class ContinuityEndpoints
{
    internal static void Map(RouteGroupBuilder admin)
    {
        AdminAutomationEndpoints.Map(admin);
        var group = admin.MapGroup("/agent-instances/{instanceId:guid}");
        group.MapGet("/maintenance", (Guid instanceId, ExperienceService service, IExperienceStore store, CancellationToken ct) =>
            Respond(async () => { await service.RequireInstanceAsync(instanceId, ct); return await store.MaintenanceSettingsAsync(instanceId, ct); }));
        group.MapPut("/maintenance", (Guid instanceId, IdentityMaintenanceConfigurationRequest request,
            ExperienceService service, IExperienceStore store, IIdGenerator ids, TimeProvider time, CancellationToken ct) => Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct);
                return await store.ConfigureMaintenanceAsync(instanceId, request.ExpectedRevision, request.AllowAgentConsolidation, ct,
                    Audit(ids, time, instanceId, "configureConsolidation", request.AllowAgentConsolidation));
            }));
        group.MapGet("/experience", (Guid instanceId, ExperienceService service, IExperienceStore store, IAgentRunStore runs, CancellationToken ct) =>
            Respond(async () => await ExperienceReview(instanceId, service, store, runs, ct)));
        group.MapPut("/experience/configuration", (Guid instanceId, ExperienceConfigurationRequest request,
            ExperienceService service, IExperienceStore store, IAgentRunStore runs, IAdminEventStore events, IIdGenerator ids, TimeProvider time, CancellationToken ct) =>
            Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct);
                await store.ConfigureAsync(instanceId, request.ExpectedRevision, request.Enabled, ct, Audit(ids, time, instanceId, "configure", request.Enabled));
                return await ExperienceReview(instanceId, service, store, runs, ct);
            }));
        group.MapPost("/experience/checkpoints", (Guid instanceId, ExperienceCheckpointRequest request,
            ExperienceService service, IExperienceStore store, IAgentRunStore runs, IAdminEventStore events, IIdGenerator ids, TimeProvider time, CancellationToken ct) =>
            Respond(async () =>
            {
                if (!Guid.TryParse(request.SessionId, out var sessionId)) throw AgentCoreErrors.Validation("Source Session is invalid.");
                var record = await service.RequestSessionAsync(instanceId, sessionId, ct);
                await events.AppendAsync(Audit(ids, time, instanceId, "checkpoint", recordId: record.ExperienceId), ct);
                return await ExperienceReview(instanceId, service, store, runs, ct);
            }));
        group.MapPut("/experience/{experienceId:guid}", (Guid instanceId, Guid experienceId, ExperienceVisibilityRequest request,
            ExperienceService service, IExperienceStore store, IAgentRunStore runs, IAdminEventStore events, IIdGenerator ids, TimeProvider time, CancellationToken ct) =>
            Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct);
                if (!Enum.GetNames<ExperienceVisibility>().Contains(request.Visibility, StringComparer.Ordinal)) throw AgentCoreErrors.Validation("Experience visibility is invalid.");
                await store.SetVisibilityAsync(instanceId, experienceId, request.ExpectedRevision, Enum.Parse<ExperienceVisibility>(request.Visibility), ct, Audit(ids, time, instanceId, request.Visibility, recordId: experienceId));
                return await ExperienceReview(instanceId, service, store, runs, ct);
            }));
        group.MapPost("/experience/reset", (Guid instanceId, ExperienceService service, IExperienceStore store, IAgentRunStore runs,
            IAdminEventStore events, IIdGenerator ids, TimeProvider time, CancellationToken ct) => Respond(async () =>
            {
                await service.RequireInstanceAsync(instanceId, ct); await store.ResetAsync(instanceId, ct, Audit(ids, time, instanceId, "reset"));
                return await ExperienceReview(instanceId, service, store, runs, ct);
            }));
        MapWork(group);
    }

    private static void MapWork(RouteGroupBuilder group)
    {
    }
    private static async Task<ExperienceReviewResponse> ExperienceReview(Guid instanceId, ExperienceService service, IExperienceStore store, IAgentRunStore runs, CancellationToken ct)
    {
        await service.RequireInstanceAsync(instanceId, ct);
        var settings = await store.SettingsAsync(instanceId, ct);
        var rows = new List<ExperienceResponse>();
        foreach (var r in await store.ListAsync(instanceId, 50, ct))
        {
            if (r.Visibility == ExperienceVisibility.Deleted) continue;
            var w = await runs.GetAsync(new(instanceId, LocalUserProfile.Id), r.GenerationAgentRunId, ct);
            rows.Add(new(r.ExperienceId.ToString("D"), r.SourceKind.ToString(), r.SourceId.ToString("D"), r.ThroughCursor,
                HttpMapping.Format(r.SourceAtUtc), r.DefinitionId, r.DefinitionVersion, r.GenerationAgentRunId.ToString("D"), r.Model.CatalogKey,
                r.Content is not null ? "Completed" : w?.Status.ToString() ?? "Pending", r.Visibility.ToString(), r.Revision,
                settings.Enabled && r.Content is not null && r.Visibility == ExperienceVisibility.Eligible,
                r.Content is { } c ? new(c.Goal, c.Attempts, c.Decisions, c.Outcomes, c.Corrections, c.Unresolved, c.Difficulties, c.Lessons) : null,
                w?.Failure?.DiagnosticId?.ToString("D"), w?.Failure?.Summary, HttpMapping.Format(r.SourceAtUtc),
                r.CheckpointAtUtc is { } checkpointAt ? HttpMapping.Format(checkpointAt) : null,
                (r.DerivedFromExperienceIds ?? []).Select(id => id.ToString("D")).ToArray(), r.MaintenanceOrigin));
        }
        return new(settings.Enabled, settings.Revision, ExperienceService.MaxContextCharacters, rows);
    }
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
