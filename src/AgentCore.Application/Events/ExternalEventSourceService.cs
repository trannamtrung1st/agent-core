using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Events;

public sealed record ExternalEventCredential(Guid SourceId, Guid SourceKey, string Token, ExternalEventSourceStatus Status);

public sealed class ExternalEventSourceService(
    IExternalEventStore store,
    IIdGenerator ids,
    TimeProvider time,
    ILogger<ExternalEventSourceService>? logger = null)
{
    public ValueTask<IReadOnlyList<ExternalEventSource>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ListAsync(cancellationToken);

    public async ValueTask<ExternalEventCredential> CreateAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var token = WebhookTokens.Create();
        var now = time.GetUtcNow();
        var source = new ExternalEventSource(
            ids.NewId(),
            displayName,
            ExternalEventSourceKind.Webhook,
            ids.NewId(),
            WebhookTokens.Hash(token),
            ExternalEventSourceStatus.Active,
            1,
            now,
            now);
        var saved = await store.CreateAsync(source, cancellationToken).ConfigureAwait(false);
        logger?.LogInformation(
            "Event source {SourceStatus} {SourceId} key {SourceKey}.",
            saved.Status,
            saved.SourceId,
            saved.SourceKey);
        return new ExternalEventCredential(saved.SourceId, saved.SourceKey, token, saved.Status);
    }

    public async ValueTask<ExternalEventCredential> RotateAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var token = WebhookTokens.Create();
        var saved = await store.SaveAsync(
            new ExternalEventSource(
                current.SourceId,
                current.DisplayName,
                current.Kind,
                current.SourceKey,
                WebhookTokens.Hash(token),
                ExternalEventSourceStatus.Active,
                current.Revision + 1,
                current.CreatedAtUtc,
                time.GetUtcNow()),
            current.Revision,
            cancellationToken).ConfigureAwait(false);
        logger?.LogInformation(
            "Event source {SourceStatus} {SourceId} key {SourceKey}.",
            saved.Status,
            saved.SourceId,
            saved.SourceKey);
        return new ExternalEventCredential(saved.SourceId, saved.SourceKey, token, saved.Status);
    }

    public async ValueTask<ExternalEventSource> RevokeAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var saved = await store.SaveAsync(
            new ExternalEventSource(
                current.SourceId,
                current.DisplayName,
                current.Kind,
                current.SourceKey,
                null,
                ExternalEventSourceStatus.Revoked,
                current.Revision + 1,
                current.CreatedAtUtc,
                time.GetUtcNow()),
            current.Revision,
            cancellationToken).ConfigureAwait(false);
        logger?.LogInformation(
            "Event source {SourceStatus} {SourceId} key {SourceKey}.",
            saved.Status,
            saved.SourceId,
            saved.SourceKey);
        return saved;
    }

    private async ValueTask<ExternalEventSource> RequireAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await store.GetAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return source ?? throw AgentCoreErrors.NotFound("Event source was not found.");
    }

}
