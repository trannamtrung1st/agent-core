using System.Text.Json;
using AgentCore.Application.Models;
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
    string EvidenceJson,
    ExecutionModelPin? Model = null);

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
        sourceKind is TriggerSourceKind.Schedule or TriggerSourceKind.ManualInvocation ? Schedule : ApplicationEvent;

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
        var admission = await TriggerDurableSchedulingPolicy.EvaluateScheduledOccurrenceEligibilityAsync(
                owner,
                instances,
                definitions,
                profiles,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.Allowed)
        {
            return new TriggerAdmissionDecision(
                TriggerAdmissionDecisionKind.Suspend,
                TriggerDurableSchedulingPolicy.PolicyMessage(admission.DenialReason!.Value));
        }

        if (!OccurrenceCompatibility.Allows(admission.Definition!, sourceKind))
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
    TimeProvider time,
    IAgentInstanceStore? instances = null,
    IAgentDefinitionStore? definitions = null,
    IModelCatalog? catalog = null)
{
    public static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LivePreparedLease = TimeSpan.FromSeconds(30);
    private Guid? _liveRepairCursor;

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

        var acceptedLive = await store.ListUnsettledLiveAsync(TriggerScheduler.DefaultBatchSize, _liveRepairCursor, cancellationToken).ConfigureAwait(false);
        _liveRepairCursor = acceptedLive.Count == TriggerScheduler.DefaultBatchSize ? acceptedLive[^1].OccurrenceId : null;
        foreach (var occurrence in acceptedLive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await guard.EvaluateAsync(occurrence.Owner, AutomationRules.AdmissionSource(occurrence), cancellationToken)
                .ConfigureAwait(false);
            var targets = directory.ListCompatible(occurrence.Owner, occurrence.SourceKind);
            if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
            {
                await store.CompleteLiveEvaluationAsync(occurrence.OccurrenceId, occurrence.LiveSessionId!.Value,
                    now, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!targets.Any(target => target.SessionId == occurrence.LiveSessionId)) continue;
            var delivery = Delivery(occurrence);
            if (await mailbox.SubmitAsync(occurrence.LiveSessionId!.Value, delivery, cancellationToken).ConfigureAwait(false)
                != OccurrenceAccept.Unavailable)
                await mailbox.BeginAcceptedAsync(occurrence.LiveSessionId.Value, delivery, cancellationToken).ConfigureAwait(false);
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
        var decision = await guard.EvaluateAsync(occurrence.Owner, AutomationRules.AdmissionSource(occurrence), cancellationToken)
            .ConfigureAwait(false);
        if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
        {
            await RejectIneligibleAsync(occurrence, decision.Reason ?? "Scheduling is disabled for this agent.", now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (occurrence.AutomationId is Guid automationId)
        {
            var registration = await store.GetAsync(occurrence.Owner, automationId, cancellationToken).ConfigureAwait(false);
            if (registration is null || registration.Status is AutomationStatus.Cancelled
                or AutomationStatus.Expired
                or AutomationStatus.SuspendedPolicy or AutomationStatus.Disabled)
            {
                await store.TryRejectPendingAsync(occurrence.OccurrenceId, "Registration is no longer active.", now, cancellationToken)
                    .ConfigureAwait(false);
                RuntimeTelemetry.RecordTriggerScheduler("rejected");
                return;
            }


        }

        var pinned = occurrence.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession
            ? occurrence : await EnsureModelAsync(occurrence, now, cancellationToken).ConfigureAwait(false);
        if (pinned is null)
        {
            return;
        }

        occurrence = pinned;
        var targets = occurrence.AutomationId is not null || AutomationRules.IsManual(occurrence)
            ? (IReadOnlyList<LiveOccurrenceTarget>)[] : directory.ListCompatible(occurrence.Owner, occurrence.SourceKind);
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

        var delivery = Delivery(occurrence);
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
        var targets = occurrence.AutomationId is not null || AutomationRules.IsManual(occurrence)
            ? (IReadOnlyList<LiveOccurrenceTarget>)[] : directory.ListCompatible(occurrence.Owner, occurrence.SourceKind);
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
        if (occurrence.LiveSessionId is { } target && target != sessionId) return;
        if (occurrence.LiveSessionId is null)
        {
            occurrence = await store.BindLiveSessionAsync(occurrence.OccurrenceId, occurrence.RoutingRevision,
                sessionId, now, cancellationToken).ConfigureAwait(false) ?? occurrence;
            if (occurrence.LiveSessionId != sessionId) return;
        }
        var delivery = Delivery(occurrence);
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

        stored = await store.BindLiveSessionAsync(occurrenceId, stored.RoutingRevision, sessionId, now, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null) return;

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

    private static OccurrenceDelivery Delivery(TriggerOccurrence occurrence) =>
        new(occurrence.OccurrenceId, occurrence.Owner, occurrence.SourceKind, occurrence.EvidenceJson, occurrence.ModelPin);

    private async Task<TriggerOccurrence?> EnsureModelAsync(
        TriggerOccurrence occurrence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (catalog is null || instances is null || definitions is null)
        {
            return occurrence;
        }

        if (occurrence.ModelPin is null)
        {
            var resolved = await ExecutionModelAdmission.ResolveAsync(
                catalog,
                instances,
                definitions,
                store,
                occurrence.Owner,
                occurrence.AutomationId,
                cancellationToken).ConfigureAwait(false);
            if (resolved is null || resolved.Pin is null)
            {
                await store.TryRejectPendingAsync(
                        occurrence.OccurrenceId,
                        resolved?.FailureCode ?? ExecutionModelPolicy.UnavailableCode,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                RuntimeTelemetry.RecordTriggerScheduler("rejected");
                return null;
            }

            var stored = await store.TryAssignModelPinIfMissingAsync(
                    occurrence.OccurrenceId,
                    resolved.Pin,
                    cancellationToken)
                .ConfigureAwait(false);
            occurrence = stored ?? occurrence.WithModelPin(resolved.Pin);
        }

        var decision = await ExecutionModelAdmission.ValidateAsync(
            catalog,
            instances,
            definitions,
            store,
            occurrence,
            cancellationToken).ConfigureAwait(false);
        if (decision is { Accepted: false })
        {
            await store.TryRejectPendingAsync(
                    occurrence.OccurrenceId,
                    decision.FailureCode ?? ExecutionModelPolicy.UnavailableCode,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            RuntimeTelemetry.RecordTriggerScheduler("rejected");
            return null;
        }

        return occurrence;
    }

    private async Task RejectIneligibleAsync(
        TriggerOccurrence occurrence,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (occurrence.AutomationId is Guid automationId)
        {
            var registration = await store.GetAsync(occurrence.Owner, automationId, cancellationToken).ConfigureAwait(false);
            if (registration is { Status: AutomationStatus.Active })
            {
                await store.SuspendPolicyAsync(
                    occurrence.Owner,
                    automationId,
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
    TimeProvider time,
    IAgentInstanceStore? instances = null,
    IAgentDefinitionStore? definitions = null,
    IModelCatalog? catalog = null) : IDurableApplicationEventIngress
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
        var pin = await ExecutionModelAdmission.ResolveAsync(
            catalog,
            instances,
            definitions,
            store,
            owner,
            automationId: null,
            cancellationToken).ConfigureAwait(false);
        if (pin is { FailureCode: not null, Pin: null })
        {
            return new DurableEventResult(DurableEventOutcome.Rejected, pin.FailureCode, null);
        }

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
            null,
            modelPin: pin?.Pin);
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
