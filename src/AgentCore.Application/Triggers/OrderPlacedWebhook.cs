using AgentCore.Application.Connections;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Connections;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Triggers;

public enum OrderPlacedAdmissionKind
{
    Admitted,
    Duplicate,
    Unauthorized,
    Invalid,
    NotAdmitted,
    ModelRejected
}

public sealed record OrderPlacedAdmission(OrderPlacedAdmissionKind Kind, Guid? OccurrenceId, string? Code);

public sealed class OrderPlacedWebhook(
    IApplicationConnectionStore connections,
    ITriggerStore store,
    ITriggerAdmissionGuard guard,
    IIdGenerator ids,
    TimeProvider time,
    ILocalUserProfileService profiles,
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IModelCatalog catalog,
    ILogger<OrderPlacedWebhook>? logger = null)
{
    public async ValueTask<bool> CredentialsMatchAsync(
        Guid webhookKey,
        string presentedToken,
        CancellationToken cancellationToken = default)
    {
        var connection = await FindAsync(webhookKey, cancellationToken).ConfigureAwait(false);
        return Authorized(connection, presentedToken);
    }

    public async ValueTask<OrderPlacedAdmission> AdmitAsync(
        Guid webhookKey,
        string presentedToken,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var connection = await FindAsync(webhookKey, cancellationToken).ConfigureAwait(false);
        if (!Authorized(connection, presentedToken))
        {
            Log(OrderPlacedAdmissionKind.Unauthorized, webhookKey, null, null);
            return new OrderPlacedAdmission(OrderPlacedAdmissionKind.Unauthorized, null, "unauthorized");
        }

        if (!OrderPlacedPayload.TryNormalize(body.Span, out var evidence, out var sourceEventId, out _))
        {
            Log(OrderPlacedAdmissionKind.Invalid, webhookKey, connection!.AgentInstanceId, null);
            return new OrderPlacedAdmission(OrderPlacedAdmissionKind.Invalid, null, "invalid_payload");
        }

        var profile = await profiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(connection!.AgentInstanceId, profile.ProfileId);
        var decision = await guard.EvaluateAsync(owner, TriggerSourceKind.ApplicationEvent, cancellationToken)
            .ConfigureAwait(false);
        if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
        {
            Log(OrderPlacedAdmissionKind.NotAdmitted, webhookKey, owner.AgentInstanceId, null);
            return new OrderPlacedAdmission(OrderPlacedAdmissionKind.NotAdmitted, null, "not_admitted");
        }

        var pin = await ExecutionModelAdmission.ResolveAsync(
            catalog,
            instances,
            definitions,
            store,
            owner,
            registrationId: null,
            cancellationToken).ConfigureAwait(false);
        if (pin is { FailureCode: not null })
        {
            Log(OrderPlacedAdmissionKind.ModelRejected, webhookKey, owner.AgentInstanceId, null);
            return new OrderPlacedAdmission(OrderPlacedAdmissionKind.ModelRejected, null, pin.FailureCode);
        }

        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var occurrence = new TriggerOccurrence(
            ids.NewId(),
            $"order.placed:{sourceEventId}",
            null,
            owner,
            TriggerSourceKind.ApplicationEvent,
            null,
            now,
            now,
            evidence,
            null,
            null,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null,
            null,
            pin?.Pin);
        var admitted = await store.AdmitOccurrenceAsync(occurrence, cancellationToken).ConfigureAwait(false);
        var duplicate = admitted.Kind == TriggerOccurrenceAdmitKind.Duplicate;
        var saved = admitted.Occurrence ?? occurrence;
        Log(
            duplicate ? OrderPlacedAdmissionKind.Duplicate : OrderPlacedAdmissionKind.Admitted,
            webhookKey,
            owner.AgentInstanceId,
            saved.OccurrenceId);
        return new OrderPlacedAdmission(
            duplicate ? OrderPlacedAdmissionKind.Duplicate : OrderPlacedAdmissionKind.Admitted,
            saved.OccurrenceId,
            null);
    }

    private async ValueTask<ApplicationConnection?> FindAsync(Guid webhookKey, CancellationToken cancellationToken)
    {
        if (webhookKey == Guid.Empty)
        {
            return null;
        }

        return await connections.GetByWebhookKeyAsync(webhookKey, cancellationToken).ConfigureAwait(false);
    }

    private static bool Authorized(ApplicationConnection? connection, string presentedToken)
    {
        var match = WebhookTokens.Matches(connection?.WebhookTokenHash, presentedToken);
        return connection is { WebhookStatus: WebhookCredentialStatus.Active } && match;
    }

    private void Log(OrderPlacedAdmissionKind kind, Guid webhookKey, Guid? agentInstanceId, Guid? occurrenceId) =>
        logger?.LogInformation(
            "order.placed {Admission} for webhook {WebhookKey} agent instance {AgentInstanceId} occurrence {OccurrenceId}.",
            kind,
            webhookKey,
            agentInstanceId,
            occurrenceId);
}
