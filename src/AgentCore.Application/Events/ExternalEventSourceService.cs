using AgentCore.Application.Connections;
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
    ITriggerStore triggers,
    ITriggerAdmissionGuard guard,
    IIdGenerator ids,
    TimeProvider time,
    ILocalUserProfileService profiles,
    IAgentInstanceStore instances,
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

    public async ValueTask<TriggerRegistration> SubscribeAsync(
        Guid agentInstanceId,
        Guid sourceId,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        if (!ExternalEventTypes.IsAllowed(eventType))
        {
            throw AgentCoreErrors.Validation("Event type is not allowed.");
        }

        await RequireInstanceAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        _ = await RequireAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var profile = await profiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(agentInstanceId, profile.ProfileId);
        var decision = await guard.EvaluateAsync(owner, TriggerSourceKind.ApplicationEvent, cancellationToken)
            .ConfigureAwait(false);
        if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
        {
            throw AgentCoreErrors.Validation("This agent cannot subscribe to application events.");
        }

        var existing = await triggers.ListAsync(owner, TriggerRegistrationStatus.Active, cancellationToken)
            .ConfigureAwait(false);
        var match = existing.FirstOrDefault(item =>
            item.EventSourceId == sourceId && item.EventType == eventType);
        if (match is not null)
        {
            return match;
        }

        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var registration = new TriggerRegistration(
            ids.NewId(),
            owner,
            TriggerRegistrationStatus.Active,
            eventType,
            new OneShotSchedule(DateTimeOffset.UnixEpoch, "UTC"),
            null,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, now, now),
            null,
            eventSourceId: sourceId,
            eventType: eventType);
        return await triggers.CreateAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<TriggerRegistration>> ListSubscriptionsAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        await RequireInstanceAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        var profile = await profiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var rows = await triggers.ListAsync(
            new TriggerOwner(agentInstanceId, profile.ProfileId),
            status: null,
            cancellationToken).ConfigureAwait(false);
        return rows.Where(item => item.EventSourceId is not null).ToArray();
    }

    private async ValueTask<ExternalEventSource> RequireAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await store.GetAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return source ?? throw AgentCoreErrors.NotFound("Event source was not found.");
    }

    private async ValueTask RequireInstanceAsync(Guid agentInstanceId, CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            throw AgentCoreErrors.NotFound("Agent instance was not found.");
        }
    }
}
