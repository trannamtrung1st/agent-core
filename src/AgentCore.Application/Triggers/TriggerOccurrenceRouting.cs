using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public enum TriggerAdmissionDecisionKind
{
    Allow,
    Suspend
}

public sealed record TriggerAdmissionDecision(TriggerAdmissionDecisionKind Kind, string? Reason);

public interface ITriggerAdmissionGuard
{
    ValueTask<TriggerAdmissionDecision> EvaluateAsync(
        TriggerOwner owner,
        TriggerSourceKind sourceKind,
        CancellationToken cancellationToken = default);
}

public enum OccurrenceAccept
{
    Accepted,
    Duplicate,
    Unavailable
}

public sealed record OccurrenceDelivery(
    Guid OccurrenceId,
    TriggerOwner Owner,
    TriggerSourceKind SourceKind,
    string EvidenceJson);

public sealed record LiveOccurrenceTarget(Guid SessionId);

public interface ILiveOccurrenceDirectory
{
    IReadOnlyList<LiveOccurrenceTarget> ListCompatible(TriggerOwner owner, TriggerSourceKind sourceKind);
}

public interface IOccurrenceMailbox
{
    Task<OccurrenceAccept> SubmitAsync(
        Guid sessionId,
        OccurrenceDelivery delivery,
        CancellationToken cancellationToken = default);

    Task<bool> BeginAcceptedAsync(
        Guid sessionId,
        OccurrenceDelivery delivery,
        CancellationToken cancellationToken = default);

    Task AbandonReservationAsync(
        Guid sessionId,
        Guid occurrenceId,
        CancellationToken cancellationToken = default);
}

public enum DurableEventOutcome
{
    Admitted,
    Duplicate,
    Rejected
}

public sealed record DurableEventResult(DurableEventOutcome Outcome, string? Reason, TriggerOccurrence? Occurrence);

public static class OccurrenceCompatibility
{
    public const string Schedule = "schedule";
    public const string ApplicationEvent = "applicationEvent";

    public static string SourceName(TriggerSourceKind sourceKind) =>
        sourceKind == TriggerSourceKind.Schedule ? Schedule : ApplicationEvent;

    public static bool Allows(AgentDefinition definition, TriggerSourceKind sourceKind)
    {
        var policy = definition.TriggerPolicy;
        return policy is { Enabled: true }
            && policy.AllowedSourceKinds.Contains(SourceName(sourceKind), StringComparer.Ordinal);
    }
}

public sealed class TriggerAdmissionGuard(
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IMemoryStore profiles) : ITriggerAdmissionGuard
{
    public async ValueTask<TriggerAdmissionDecision> EvaluateAsync(
        TriggerOwner owner,
        TriggerSourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.FindAsync(owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return new TriggerAdmissionDecision(TriggerAdmissionDecisionKind.Suspend, "Agent instance is unavailable.");
        }

        if (instance.Lifecycle != AgentInstanceLifecycle.Active)
        {
            return new TriggerAdmissionDecision(TriggerAdmissionDecisionKind.Suspend, "Agent instance is not active.");
        }

        var profile = await profiles.LoadProfileAsync(owner.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return new TriggerAdmissionDecision(TriggerAdmissionDecisionKind.Suspend, "Owner profile is unavailable.");
        }

        var definition = await TriggerDurableSchedulingPolicy.ResolveEffectiveDefinitionAsync(
                new TriggerOwner(owner.AgentInstanceId, owner.ProfileId),
                instances,
                definitions,
                cancellationToken)
            .ConfigureAwait(false);
        if (definition is null || !OccurrenceCompatibility.Allows(definition, sourceKind))
        {
            return new TriggerAdmissionDecision(TriggerAdmissionDecisionKind.Suspend, "Scheduling is disabled for this agent.");
        }

        return new TriggerAdmissionDecision(TriggerAdmissionDecisionKind.Allow, null);
    }
}

public sealed class TriggerOccurrenceRouter(
    ITriggerStore store,
    ITriggerAdmissionGuard guard,
    ILiveOccurrenceDirectory directory,
    IOccurrenceMailbox mailbox,
    IIdGenerator ids,
    TimeProvider time)
{
    public static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LivePreparedLease = TimeSpan.FromSeconds(30);

    public async Task RouteOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        await store.RecoverExpiredClaimsAsync(now, cancellationToken).ConfigureAwait(false);
        var prepared = await store.ListByDispositionAsync(
            OccurrenceRoutingDisposition.LivePrepared,
            TriggerScheduler.DefaultBatchSize,
            cancellationToken).ConfigureAwait(false);
        foreach (var occurrence in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResumePreparedAsync(occurrence, now, cancellationToken).ConfigureAwait(false);
        }

        var pending = await store.ListByDispositionAsync(
            OccurrenceRoutingDisposition.Pending,
            TriggerScheduler.DefaultBatchSize,
            cancellationToken).ConfigureAwait(false);
        foreach (var occurrence in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RouteOneAsync(occurrence, now, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<TriggerOccurrence>> ListAwaitingDurableWorkAsync(
        CancellationToken cancellationToken = default) =>
        await store.ListByDispositionAsync(
            OccurrenceRoutingDisposition.AwaitingDurableWork,
            TriggerScheduler.DefaultBatchSize,
            cancellationToken).ConfigureAwait(false);

    private async Task RouteOneAsync(
        TriggerOccurrence occurrence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var decision = await guard.EvaluateAsync(occurrence.Owner, occurrence.SourceKind, cancellationToken)
            .ConfigureAwait(false);
        if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
        {
            await RejectIneligibleAsync(occurrence, decision.Reason ?? "Scheduling is disabled for this agent.", now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (occurrence.RegistrationId is Guid registrationId)
        {
            var registration = await store.GetAsync(occurrence.Owner, registrationId, cancellationToken).ConfigureAwait(false);
            if (registration is null || registration.Status is TriggerRegistrationStatus.Cancelled
                or TriggerRegistrationStatus.Expired
                or TriggerRegistrationStatus.SuspendedPolicy)
            {
                await store.TryRejectPendingAsync(occurrence.OccurrenceId, "Registration is no longer active.", now, cancellationToken)
                    .ConfigureAwait(false);
                RuntimeTelemetry.RecordTriggerScheduler("rejected");
                return;
            }

            if (occurrence.ScheduleRevision != registration.ScheduleRevision)
            {
                await store.TryRejectPendingAsync(occurrence.OccurrenceId, "Schedule was superseded.", now, cancellationToken)
                    .ConfigureAwait(false);
                RuntimeTelemetry.RecordTriggerScheduler("rejected");
                return;
            }
        }

        var targets = directory.ListCompatible(occurrence.Owner, occurrence.SourceKind);
        var claimId = ids.NewId();
        var claimed = await store.TryClaimOccurrenceAsync(
            occurrence.OccurrenceId,
            claimId,
            now.Add(ClaimLease),
            now,
            cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            return;
        }

        if (targets.Count != 1)
        {
            var reason = targets.Count == 0
                ? "No compatible runtime."
                : "Multiple compatible runtimes.";
            await store.MarkAwaitingDurableWorkAsync(occurrence.OccurrenceId, claimId, reason, now, cancellationToken)
                .ConfigureAwait(false);
            RuntimeTelemetry.RecordTriggerScheduler("awaiting_durable_work");
            return;
        }

        var delivery = new OccurrenceDelivery(
            occurrence.OccurrenceId,
            occurrence.Owner,
            occurrence.SourceKind,
            occurrence.EvidenceJson);
        var accepted = await mailbox.SubmitAsync(targets[0].SessionId, delivery, cancellationToken).ConfigureAwait(false);
        if (accepted == OccurrenceAccept.Unavailable)
        {
            await store.ReleaseClaimAsync(occurrence.OccurrenceId, claimId, now, cancellationToken).ConfigureAwait(false);
            RuntimeTelemetry.RecordTriggerScheduler("released");
            return;
        }

        await CommitAndBeginAsync(targets[0].SessionId, occurrence.OccurrenceId, claimId, delivery, now, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ResumePreparedAsync(
        TriggerOccurrence occurrence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var targets = directory.ListCompatible(occurrence.Owner, occurrence.SourceKind);
        var leaseExpired = occurrence.ClaimLeaseExpiresAtUtc is DateTimeOffset lease && lease <= now;
        if (!leaseExpired)
        {
            if (targets.Count != 1)
            {
                return;
            }

            await TryCompletePreparedAsync(occurrence, targets[0].SessionId, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (targets.Count != 1)
        {
            var reason = targets.Count == 0
                ? "No compatible runtime."
                : "Multiple compatible runtimes.";
            await store.PromoteLivePreparedAwaitingDurableWorkAsync(
                occurrence.OccurrenceId,
                occurrence.RoutingRevision,
                reason,
                now,
                cancellationToken).ConfigureAwait(false);
            RuntimeTelemetry.RecordTriggerScheduler("awaiting_durable_work");
            return;
        }

        await TryCompletePreparedAsync(occurrence, targets[0].SessionId, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryCompletePreparedAsync(
        TriggerOccurrence occurrence,
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var delivery = new OccurrenceDelivery(
            occurrence.OccurrenceId,
            occurrence.Owner,
            occurrence.SourceKind,
            occurrence.EvidenceJson);
        var accept = await mailbox.SubmitAsync(sessionId, delivery, cancellationToken).ConfigureAwait(false);
        if (accept == OccurrenceAccept.Unavailable)
        {
            return;
        }

        await FinishBeginAsync(
            sessionId,
            occurrence.OccurrenceId,
            occurrence.RoutingRevision,
            delivery,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CommitAndBeginAsync(
        Guid sessionId,
        Guid occurrenceId,
        Guid claimId,
        OccurrenceDelivery delivery,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stored = await store.TryAcceptLiveAsync(occurrenceId, claimId, now, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            await mailbox.AbandonReservationAsync(sessionId, occurrenceId, cancellationToken).ConfigureAwait(false);
            await store.ReleaseClaimAsync(occurrenceId, claimId, now, cancellationToken).ConfigureAwait(false);
            RuntimeTelemetry.RecordTriggerScheduler("released");
            return;
        }

        await FinishBeginAsync(sessionId, occurrenceId, stored.RoutingRevision, delivery, now, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task FinishBeginAsync(
        Guid sessionId,
        Guid occurrenceId,
        long preparedRoutingRevision,
        OccurrenceDelivery delivery,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool started;
        try
        {
            started = await mailbox.BeginAcceptedAsync(sessionId, delivery, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RuntimeTelemetry.RecordTriggerScheduler("live_prepared");
            throw;
        }
        catch
        {
            await ReconcileDefiniteBeginFailureAsync(
                sessionId,
                occurrenceId,
                preparedRoutingRevision,
                now,
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (!started)
        {
            await ReconcileDefiniteBeginFailureAsync(
                sessionId,
                occurrenceId,
                preparedRoutingRevision,
                now,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var confirmed = await store.ConfirmLiveBeginAsync(
            occurrenceId,
            preparedRoutingRevision,
            now,
            cancellationToken).ConfigureAwait(false);
        if (confirmed is null)
        {
            RuntimeTelemetry.RecordTriggerScheduler("live_prepared");
            return;
        }

        RuntimeTelemetry.RecordTriggerScheduler("accepted_live");
    }

    private async Task ReconcileDefiniteBeginFailureAsync(
        Guid sessionId,
        Guid occurrenceId,
        long preparedRoutingRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await store.RevertLivePreparedAsync(occurrenceId, preparedRoutingRevision, now, cancellationToken).ConfigureAwait(false);
        await mailbox.AbandonReservationAsync(sessionId, occurrenceId, cancellationToken).ConfigureAwait(false);
        RuntimeTelemetry.RecordTriggerScheduler("released");
    }

    private async Task RejectIneligibleAsync(
        TriggerOccurrence occurrence,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (occurrence.RegistrationId is Guid registrationId)
        {
            var registration = await store.GetAsync(occurrence.Owner, registrationId, cancellationToken).ConfigureAwait(false);
            if (registration is { Status: TriggerRegistrationStatus.Active })
            {
                await store.SuspendPolicyAsync(
                    occurrence.Owner,
                    registrationId,
                    registration.Revision,
                    reason,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await store.TryRejectPendingAsync(occurrence.OccurrenceId, reason, now, cancellationToken).ConfigureAwait(false);
        RuntimeTelemetry.RecordTriggerScheduler("rejected");
    }
}

public sealed class DurableOrderEventIngress(
    ITriggerStore store,
    ITriggerAdmissionGuard guard,
    IIdGenerator ids,
    TimeProvider time) : IDurableApplicationEventIngress
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record OrderStatusEvidence(string OrderReference, string Status, string Evidence);
    public async ValueTask<DurableEventResult> PublishOrderStatusAsync(
        TriggerOwner owner,
        Guid eventId,
        string orderReference,
        string status,
        string? evidence,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(eventId, orderReference, status, evidence, out var normalized, out var error))
        {
            return new DurableEventResult(DurableEventOutcome.Rejected, error, null);
        }

        var decision = await guard.EvaluateAsync(owner, TriggerSourceKind.ApplicationEvent, cancellationToken)
            .ConfigureAwait(false);
        if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
        {
            return new DurableEventResult(DurableEventOutcome.Rejected, decision.Reason, null);
        }

        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var occurrence = new TriggerOccurrence(
            ids.NewId(),
            $"applicationEvent:{eventId:D}",
            null,
            owner,
            TriggerSourceKind.ApplicationEvent,
            null,
            now,
            now,
            normalized,
            eventId,
            null,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null);
        var admitted = await store.AdmitOccurrenceAsync(occurrence, cancellationToken).ConfigureAwait(false);
        return admitted.Kind == TriggerOccurrenceAdmitKind.Duplicate
            ? new DurableEventResult(DurableEventOutcome.Duplicate, null, admitted.Occurrence)
            : new DurableEventResult(DurableEventOutcome.Admitted, null, admitted.Occurrence);
    }

    private static bool TryNormalize(
        Guid eventId,
        string orderReference,
        string status,
        string? evidence,
        out string normalized,
        out string? error)
    {
        normalized = "";
        error = null;
        if (eventId == Guid.Empty)
        {
            error = "Event id is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(orderReference) || orderReference.Trim().Length is < 1 or > 64)
        {
            error = "Order reference must be 1..64 characters.";
            return false;
        }

        if (status is not ("shipped" or "delayed" or "delivered"))
        {
            error = "Order status is not allowlisted.";
            return false;
        }

        var reference = orderReference.Trim();
        var body = evidence ?? "";
        normalized = JsonSerializer.Serialize(new OrderStatusEvidence(reference, status, body), EvidenceJsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(normalized) > TriggerLimits.MaxEvidenceBytes)
        {
            error = "Evidence is too large.";
            normalized = "";
            return false;
        }

        return true;
    }
}

public interface IDurableApplicationEventIngress
{
    ValueTask<DurableEventResult> PublishOrderStatusAsync(
        TriggerOwner owner,
        Guid eventId,
        string orderReference,
        string status,
        string? evidence,
        CancellationToken cancellationToken = default);
}
