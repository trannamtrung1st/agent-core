using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Execution;

public sealed record BackgroundAdmissionPass(int Accepted, int Existing, int Skipped);

/// <summary>Admits occurrence input into a real Session; execution remains owned by its runtime.</summary>
public sealed class BackgroundOccurrenceIntake(
    ITriggerStore triggers,
    IAgentRunStore runs,
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IMemoryStore memory,
    IModelCatalog models,
    IIdGenerator ids,
    TimeProvider time)
{
    public async ValueTask<BackgroundAdmissionPass> AcceptAwaitingAsync(CancellationToken cancellationToken = default)
    {
        var awaiting = await triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.AwaitingDurableWork,
            TriggerScheduler.DefaultBatchSize, cancellationToken).ConfigureAwait(false);
        var accepted = 0;
        var existing = 0;
        var skipped = 0;
        foreach (var occurrence in awaiting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proposal = await ProposeAsync(occurrence, cancellationToken).ConfigureAwait(false);
            if (proposal is null)
            {
                skipped++;
                continue;
            }

            try
            {
                var result = await runs.AdmitOccurrenceAsync(proposal.Value.Session, proposal.Value.Run,
                    occurrence.RoutingRevision, cancellationToken).ConfigureAwait(false);
                if (result.Created) accepted++; else existing++;
            }
            catch (AgentCoreException exception) when (exception.Code == "Conflict")
            {
                if (exception.Message.Contains("queue is full", StringComparison.Ordinal))
                    await RejectTargetAsync(occurrence, "target-queue-overflow", cancellationToken).ConfigureAwait(false);
                // Another intake/router changed this receipt. Re-read on the next bounded pass.
                skipped++;
            }
        }
        return new BackgroundAdmissionPass(accepted, existing, skipped);
    }

    private async ValueTask<(SessionSnapshot Session, AgentRun Run)?> ProposeAsync(
        TriggerOccurrence occurrence, CancellationToken cancellationToken)
    {
        var eligibility = await TriggerDurableSchedulingPolicy.EvaluateScheduledOccurrenceEligibilityAsync(
            occurrence.Owner, instances, definitions, memory, cancellationToken).ConfigureAwait(false);
        if (!eligibility.Allowed
            || !OccurrenceCompatibility.Allows(eligibility.Definition!, AutomationRules.AdmissionSource(occurrence)))
            return null;
        var instance = await instances.FindAsync(occurrence.Owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null || instance.Lifecycle != AgentInstanceLifecycle.Active) return null;
        var definition = eligibility.Definition!;
        Automation? automation = null;
        if (occurrence.AutomationId is { } automationId)
        {
            automation = await triggers.GetAsync(occurrence.Owner, automationId, cancellationToken).ConfigureAwait(false);
            // A one-shot can be Expired after admitting its final due occurrence. Disabled/cancelled
            // registrations cannot admit even if routing previously found them eligible.
            if (automation is null || automation.Status is AutomationStatus.Disabled or AutomationStatus.Cancelled
                or AutomationStatus.SuspendedPolicy)
                return null;
        }

        if (occurrence.ExecutionTarget.SessionId is { } targetId)
        {
            var target = await memory.LoadMetadataAsync(targetId, cancellationToken).ConfigureAwait(false);
            if (!AutomationDestinationPolicy.Eligible(target, occurrence.Owner))
            {
                await RejectTargetAsync(occurrence, "target-unavailable", cancellationToken).ConfigureAwait(false);
                return null;
            }
            ExecutionModelPin selected;
            try { selected = AutomationDestinationPolicy.Pin(target!, models, requiresVision: automation?.RequiresVision == true, requiresTools: automation?.RequiresTools == true); }
            catch (AgentCoreException)
            { await RejectTargetAsync(occurrence, "target-model-unavailable", cancellationToken).ConfigureAwait(false); return null; }
            var catalog = await new EffectiveSkillCatalogResolver(instances).ResolveAsync(instance.InstanceId,
                target!.Definition, cancellationToken).ConfigureAwait(false);
            var triggerKind = occurrence.SourceKind switch { TriggerSourceKind.Schedule => TriggerKind.ScheduledOccurrence,
                TriggerSourceKind.ApplicationEvent => TriggerKind.ApplicationEvent, _ => TriggerKind.ManualInvocation };
            var activationKind = occurrence.SourceKind switch { TriggerSourceKind.Schedule => ActivationKind.ScheduledWork,
                TriggerSourceKind.ApplicationEvent => ActivationKind.ApplicationEvent, _ => ActivationKind.ManualBackground };
            var evidence = System.Text.Json.JsonSerializer.Serialize(new AgentRunAdmissionFactory.SignalInput(triggerKind,
                occurrence.EvidenceJson, null));
            var activation = new Activation(ids.NewId(), targetId, activationKind, [], occurrence.SourceEventId,
                occurrence.OccurrenceId, null, null, $"automation:{occurrence.OccurrenceId:D}", time.GetUtcNow(), evidence);
            var run = AgentRun.Create(ids.NewId(), new(occurrence.Owner.AgentInstanceId, occurrence.Owner.ProfileId),
                new(activation, target.Definition.Id, target.Definition.Version, target.PinnedPersona ?? target.Definition.Identity,
                    ids.NewId(), AgentRunOutputContract.ConversationResponse),
                new(selected.CatalogKey, selected.ProviderAlias, selected.ModelId, selected.ReasoningEffort),
                AgentRunLimits.DefaultMaxAttempts, time.GetUtcNow(), catalog,
                catalog.Where(skill => skill.Projection == SkillProjection.Always).Select(skill => skill.Key).ToArray());
            return (target, run);
        }
        var pin = occurrence.ModelPin;
        if (pin is null)
        {
            var resolved = ExecutionModelPolicy.Resolve(models, definition, instance, automation);
            if (!resolved.Accepted) return null;
            var pinned = await triggers.TryAssignModelPinIfMissingAsync(occurrence.OccurrenceId, resolved.Pin!,
                cancellationToken).ConfigureAwait(false);
            pin = pinned?.ModelPin;
            if (pin is null) return null;
        }
        if (!ExecutionModelPolicy.Validate(models, pin, definition, automation).Accepted) return null;
        var skills = await new EffectiveSkillCatalogResolver(instances).ResolveAsync(instance.InstanceId,
            definition, cancellationToken).ConfigureAwait(false);
        return BackgroundSessionAdmissionFactory.ForOccurrence(ids.NewSessionId(), ids.NewId(), ids.NewId(),
            ids.NewId(), ids.NewId(), occurrence, definition, instance.Persona, instance.PersonaRevision,
            pin, skills, time.GetUtcNow(), automation?.Name);
    }
    private async ValueTask RejectTargetAsync(TriggerOccurrence occurrence, string reason, CancellationToken ct)
    {
        await triggers.RejectAwaitingDurableWorkAsync(occurrence.OccurrenceId, occurrence.RoutingRevision, reason, time.GetUtcNow(), ct).ConfigureAwait(false);
    }
}
