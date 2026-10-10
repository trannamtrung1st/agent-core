using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api;

internal static class AdminWebhookEventEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        var group = admin.MapGroup("/connections/events");
        group.MapGet("/catalog", (UnifiedEventCatalog catalog, string? kind, CancellationToken ct) => Respond(async () => await catalog.ListAsync(ct, kind)));
        group.MapGet("/catalog/builtin/{key}", (string key, ILocalUserProfileService profiles, CancellationToken ct) => Respond(async () =>
        { await profiles.GetLocalProfileAsync(ct); return UnifiedEventCatalog.Builtin(key); }));
        group.MapGet("", (IExternalEventStore events, ITriggerStore automations, CancellationToken ct) => Respond(async () =>
        {
            var rows = new List<AdminWebhookEventResponse>();
            foreach (var item in await events.ListAsync(ct))
            {
                var subscriptions = await automations.ListEventSubscriptionsAsync(item.ResourceId, activeOnly: false, cancellationToken: ct);
                var activity = await events.ReadActivityAsync(item.ResourceId, ct);
                rows.Add(AdminHttpMapping.ToWebhookEvent(item, subscriptions.Count, activity.Receipts.FirstOrDefault()?.AdmittedAtUtc,
                    subscriptions.Count(a => a.Status == AutomationStatus.Active && a.Triggers.Any(t => t.Enabled && t.Configuration is EventTrigger e && e.EventId == item.ResourceId))));
            }
            return new AdminWebhookEventListResponse(rows);
        }));
        group.MapGet("/{eventId:guid}", (Guid eventId, IExternalEventStore events, ITriggerStore automations, CancellationToken ct) => Respond(async () =>
        {
            var item = await events.GetAsync(eventId, ct) ?? throw AgentCoreErrors.NotFound("Event was not found.");
            var subscriptions = await automations.ListEventSubscriptionsAsync(eventId, activeOnly: false, cancellationToken: ct);
            var activity = await events.ReadActivityAsync(eventId, ct);
            var receipts = activity.Receipts.ToDictionary(r => r.EventId);
            return new AdminWebhookEventDetailsResponse(
                AdminHttpMapping.ToWebhookEvent(item, subscriptions.Count, activity.Receipts.FirstOrDefault()?.AdmittedAtUtc,
                    subscriptions.Count(a => a.Status == AutomationStatus.Active && a.Triggers.Any(t => t.Enabled && t.Configuration is EventTrigger e && e.EventId == item.ResourceId))),
                subscriptions.Select(a => new AdminWebhookEventSubscriber(a.AutomationId.ToString("D"), a.Name, a.Owner.AgentInstanceId.ToString("D"), a.Status.ToString())).ToArray(),
                activity.Deliveries.Select(d => new AdminWebhookEventDelivery(d.EventId.ToString("D"), receipts[d.EventId].SourceEventId,
                    receipts[d.EventId].AdmittedAtUtc.ToString("O"), d.AutomationId.ToString("D"), d.AgentInstanceId.ToString("D"), d.Status.ToString(), d.TriggerId.ToString("D"))).ToArray(),
                activity.Receipts.Select(r => new AdminWebhookEventSignal(r.EventId.ToString("D"), r.SourceEventId, r.AdmittedAtUtc.ToString("O"))).ToArray());
        }));
        group.MapPost("", (AdminCreateWebhookEventRequest request, WebhookEventService events, CancellationToken ct) =>
            Respond(async () => AdminHttpMapping.ToWebhookEventCredential(await events.CreateAsync(request.DisplayName, request.EventKey, ct))));
        group.MapPut("/{eventId:guid}", (Guid eventId, AdminRenameWebhookEventRequest request, WebhookEventService events, CancellationToken ct) =>
            Respond(async () => AdminHttpMapping.ToWebhookEvent(await events.RenameAsync(eventId, request.DisplayName, request.ExpectedRevision, ct))));
        group.MapPost("/{eventId:guid}/rotate", (Guid eventId, WebhookEventService events, CancellationToken ct) =>
            Respond(async () => AdminHttpMapping.ToWebhookEventCredential(await events.RotateAsync(eventId, ct))));
        group.MapPost("/{eventId:guid}/revoke", (Guid eventId, WebhookEventService events, CancellationToken ct) =>
            Respond(async () => AdminHttpMapping.ToWebhookEvent(await events.RevokeAsync(eventId, ct))));
    }

    private static async Task<IResult> Respond<T>(Func<Task<T>> action)
    {
        try { return Results.Json(await action()); }
        catch (AgentCoreException e) { return ProblemResults.From(e); }
        catch (ArgumentException e) { return ProblemResults.From(AgentCoreErrors.Validation(e.Message)); }
    }
}
