using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Events;

public sealed record ExternalEventCredential(Guid ResourceId, string EventKey, string Token, WebhookEventStatus Status);

public sealed class WebhookEventService(
    IExternalEventStore store,
    IIdGenerator ids,
    TimeProvider time,
    ILogger<WebhookEventService>? logger = null)
{
    public ValueTask<IReadOnlyList<WebhookEvent>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ListAsync(cancellationToken);

    public async ValueTask<ExternalEventCredential> CreateAsync(
        string displayName,
        string eventKey,
        CancellationToken cancellationToken = default)
    {
        var token = WebhookTokens.Create();
        var now = time.GetUtcNow();
        var source = new WebhookEvent(
            ids.NewId(),
            displayName,
            WebhookEventKind.Webhook,
            EventKeys.Require(eventKey),
            WebhookTokens.Hash(token),
            WebhookEventStatus.Active,
            1,
            now,
            now);
        var saved = await store.CreateAsync(source, cancellationToken).ConfigureAwait(false);
        logger?.LogInformation(
            "Event {SourceStatus} {ResourceId} key {EventKey}.",
            saved.Status,
            saved.ResourceId,
            saved.EventKey);
        return new ExternalEventCredential(saved.ResourceId, saved.EventKey, token, saved.Status);
    }

    public async ValueTask<ExternalEventCredential> RotateAsync(
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireAsync(resourceId, cancellationToken).ConfigureAwait(false);
        var token = WebhookTokens.Create();
        var saved = await store.SaveAsync(
            new WebhookEvent(
                current.ResourceId,
                current.DisplayName,
                current.Kind,
                current.EventKey,
                WebhookTokens.Hash(token),
                WebhookEventStatus.Active,
                current.Revision + 1,
                current.CreatedAtUtc,
                time.GetUtcNow()),
            current.Revision,
            cancellationToken).ConfigureAwait(false);
        logger?.LogInformation(
            "Event {SourceStatus} {ResourceId} key {EventKey}.",
            saved.Status,
            saved.ResourceId,
            saved.EventKey);
        return new ExternalEventCredential(saved.ResourceId, saved.EventKey, token, saved.Status);
    }

    public async ValueTask<WebhookEvent> RevokeAsync(
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireAsync(resourceId, cancellationToken).ConfigureAwait(false);
        var saved = await store.SaveAsync(
            new WebhookEvent(
                current.ResourceId,
                current.DisplayName,
                current.Kind,
                current.EventKey,
                null,
                WebhookEventStatus.Revoked,
                current.Revision + 1,
                current.CreatedAtUtc,
                time.GetUtcNow()),
            current.Revision,
            cancellationToken).ConfigureAwait(false);
        logger?.LogInformation(
            "Event {SourceStatus} {ResourceId} key {EventKey}.",
            saved.Status,
            saved.ResourceId,
            saved.EventKey);
        return saved;
    }

    public async ValueTask<WebhookEvent> RenameAsync(Guid resourceId, string displayName, long expectedRevision, CancellationToken ct = default)
    {
        var current = await RequireAsync(resourceId, ct);
        return await store.SaveAsync(new WebhookEvent(current.ResourceId, displayName, current.Kind, current.EventKey,
            current.CredentialHash, current.Status, current.Revision + 1, current.CreatedAtUtc, time.GetUtcNow()), expectedRevision, ct);
    }

    private async ValueTask<WebhookEvent> RequireAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        var source = await store.GetAsync(resourceId, cancellationToken).ConfigureAwait(false);
        return source ?? throw AgentCoreErrors.NotFound("Event was not found.");
    }

}
