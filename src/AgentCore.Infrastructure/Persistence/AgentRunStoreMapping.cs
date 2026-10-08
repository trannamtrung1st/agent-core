using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Persistence;

internal static class AgentRunStoreMapping
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Indexed relational identity is authoritative; payload holds the immutable pins and bounded state.
    private sealed record RunPayload(AgentRunAdmission Admission, AgentRunModelPin PinnedModel,
        int AttemptCount, int MaxAttempts, AgentRunClaim? Claim, bool CancellationRequested,
        DateTimeOffset? CancellationRequestedAtUtc, string? KnownEffectSummary,
        AgentRunProgress? Progress, AgentRunCheckpoint? Checkpoint, AgentRunResult? Result,
        AgentRunFailure? Failure, AgentRunSideEffect SideEffect, AgentRunApproval? Approval,
        IReadOnlyList<EffectiveSkill> PinnedSkillCatalog, IReadOnlyList<string> ActiveSkillKeys,
        int SkillLoadCount, IReadOnlyList<string> LoadedCapabilityIds, int CapabilityLoadCount);

    public static AgentRunRecord ToRecord(AgentRun run)
    {
        var row = new AgentRunRecord();
        Apply(row, run);
        return row;
    }

    public static void Apply(AgentRunRecord row, AgentRun run)
    {
        row.AgentRunId = run.AgentRunId.ToString("D");
        row.ActivationId = run.ActivationId.ToString("D");
        row.SessionId = run.SessionId.ToString("D");
        row.AgentInstanceId = run.AgentInstanceId.ToString("D");
        row.ProfileId = run.ProfileId.ToString("D");
        row.Status = (int)run.Status;
        row.Revision = run.Revision;
        row.NextRetryAtUtc = run.NextRetryAtUtc?.ToUnixTimeMilliseconds();
        row.LeaseExpiresAtUtc = run.Claim?.LeaseExpiresAtUtc.ToUnixTimeMilliseconds();
        row.ApprovalExpiresAtUtc = run.Approval?.ExpiresAtUtc.ToUnixTimeMilliseconds();
        row.CreatedAtUtc = run.CreatedAtUtc.ToUnixTimeMilliseconds();
        row.UpdatedAtUtc = run.UpdatedAtUtc.ToUnixTimeMilliseconds();
        row.PayloadJson = JsonSerializer.Serialize(new RunPayload(run.Admission, run.PinnedModel,
            run.AttemptCount, run.MaxAttempts, run.Claim, run.CancellationRequested,
            run.CancellationRequestedAtUtc, run.KnownEffectSummary, run.Progress, run.Checkpoint,
            run.Result, run.Failure, run.SideEffect, run.Approval, run.PinnedSkillCatalog,
            run.ActiveSkillKeys, run.SkillLoadCount, run.LoadedCapabilityIds, run.CapabilityLoadCount), Json);
    }

    public static AgentRun ToDomain(AgentRunRecord row)
    {
        var p = JsonSerializer.Deserialize<RunPayload>(row.PayloadJson, Json)
            ?? throw AgentCoreErrors.Persistence("AgentRun state is missing.");
        if (p.Admission.Activation.ActivationId.ToString("D") != row.ActivationId
            || p.Admission.Activation.SessionId.ToString("D") != row.SessionId)
            throw AgentCoreErrors.Persistence("AgentRun identity does not match its admission.");
        return new AgentRun(Guid.Parse(row.AgentRunId),
            new AgentRunOwner(Guid.Parse(row.AgentInstanceId), Guid.Parse(row.ProfileId)),
            p.Admission, p.PinnedModel, (AgentRunStatus)row.Status, row.Revision, p.AttemptCount, p.MaxAttempts,
            row.NextRetryAtUtc is { } retry ? DateTimeOffset.FromUnixTimeMilliseconds(retry) : null,
            p.Claim, p.CancellationRequested, p.CancellationRequestedAtUtc, p.KnownEffectSummary,
            p.Progress, p.Checkpoint, p.Result, p.Failure, p.SideEffect, p.Approval,
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc),
            p.PinnedSkillCatalog, p.ActiveSkillKeys, p.SkillLoadCount, p.LoadedCapabilityIds, p.CapabilityLoadCount);
    }

    public static ActivationRecord ToActivationRecord(SessionSnapshot snapshot, AgentRun run) => new()
    {
        ActivationId = run.ActivationId.ToString("D"), SessionId = run.SessionId.ToString("D"),
        AgentInstanceId = run.AgentInstanceId.ToString("D"), ProfileId = run.ProfileId.ToString("D"),
        DedupeKey = run.Admission.Activation.DedupeKey, BackgroundSourceKey = BackgroundSourceKey(snapshot, run),
        AdmissionHash = AdmissionHash(snapshot, run),
        PayloadJson = JsonSerializer.Serialize(run.Admission.Activation, Json),
        AdmittedAtUtc = run.Admission.Activation.AdmittedAtUtc.ToUnixTimeMilliseconds()
    };

    public static string? BackgroundSourceKey(SessionSnapshot snapshot, AgentRun run)
    {
        if (snapshot.Origin.InitialBackgroundAgentRunId != run.AgentRunId) return null;
        return snapshot.Origin.Kind switch
        {
            SessionOriginKind.AutomationOccurrence => $"occurrence:{snapshot.Origin.TriggerOccurrenceId:D}",
            SessionOriginKind.ImmediateBackground => $"immediate:{snapshot.Origin.OriginatingSessionId:D}:{snapshot.Origin.OriginatingAgentRunId:D}:{run.Admission.Activation.DedupeKey}",
            SessionOriginKind.ManualBackground => $"manual:{run.Admission.Activation.DedupeKey}",
            _ => null
        };
    }

    public static string AdmissionHash(SessionSnapshot snapshot, AgentRun run)
    {
        var a = run.Admission.Activation;
        var entries = a.SourceEntryIds.Select(id => snapshot.Entries.Single(entry => entry.EntryId == id))
            .Select(entry => new { entry.Role, entry.Text, entry.SourceAdmissionFingerprint, entry.Attachments }).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            run.Owner, a.Kind, a.SourceEventId, a.TriggerOccurrenceId, a.SourceSessionId, a.SourceAgentRunId,
            SourceFingerprint = BackgroundSourceKey(snapshot, run) is null ? a.SourceFingerprint : null,
            run.DefinitionId, run.DefinitionVersion, run.PinnedPersona, run.PinnedModel, run.PinnedSkillCatalog,
            run.ActiveSkillKeys, run.MaxAttempts, OriginKind = snapshot.Origin.Kind, snapshot.Origin.AutomationId,
            snapshot.Origin.OriginatingSessionId, snapshot.Origin.OriginatingAgentRunId,
            snapshot.Origin.ReportCompletionToOrigin, Entries = entries
        }, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static void ValidateAdmission(SessionSnapshot snapshot, AgentRun run)
    {
        if (!run.IsInitialQueued || snapshot.SessionId != run.SessionId
            || snapshot.AgentInstanceId != run.AgentInstanceId || snapshot.ProfileId != run.ProfileId
            || snapshot.Definition.Id != run.DefinitionId || snapshot.Definition.Version != run.DefinitionVersion
            || snapshot.DurablyDeletedAt is not null || snapshot.ArchivedAt is not null
            || snapshot.Status is SessionStatus.Ended or SessionStatus.Ending
            || snapshot.LifecycleStatus is SessionLifecycleStatus.Completed or SessionLifecycleStatus.Cancelled or SessionLifecycleStatus.Expired or SessionLifecycleStatus.Ended)
            throw AgentCoreErrors.Validation("AgentRun admission requires an eligible owned Session and initial queued run.");
        foreach (var id in run.Admission.Activation.SourceEntryIds)
        {
            var source = snapshot.Entries.SingleOrDefault(entry => entry.EntryId == id);
            if (source is null || source.Status != EntryStatus.Completed
                || run.Admission.Activation.Kind == ActivationKind.UserTurn && source.Role != ConversationRole.User)
                throw AgentCoreErrors.Validation("Activation sources must be accepted Session entries.");
        }
        if (snapshot.Origin.InitialBackgroundAgentRunId == run.AgentRunId)
        {
            var a = run.Admission.Activation;
            var matches = snapshot.Origin.Kind switch
            {
                SessionOriginKind.ImmediateBackground => a.Kind == ActivationKind.ImmediateBackground
                    && a.SourceSessionId == snapshot.Origin.OriginatingSessionId
                    && a.SourceAgentRunId == snapshot.Origin.OriginatingAgentRunId,
                SessionOriginKind.AutomationOccurrence => a.Kind is ActivationKind.ScheduledWork or ActivationKind.ApplicationEvent or ActivationKind.ManualBackground
                    && a.TriggerOccurrenceId == snapshot.Origin.TriggerOccurrenceId,
                SessionOriginKind.ManualBackground => a.Kind == ActivationKind.ManualBackground,
                _ => false
            };
            if (!matches) throw AgentCoreErrors.Validation("Initial background activation does not match Session origin.");
        }
    }

    public static void ValidateImmediateSource(AgentRun child, AgentRun? source, bool sourceSessionEligible)
    {
        if (child.Admission.Activation.Kind != ActivationKind.ImmediateBackground) return;
        if (!sourceSessionEligible || source is null || source.Owner != child.Owner
            || source.SessionId != child.Admission.Activation.SourceSessionId
            || source.Status != AgentRunStatus.Running || source.CancellationRequested
            || source.Claim!.LeaseExpiresAtUtc <= child.CreatedAtUtc
            || source.Admission.Activation.Kind != ActivationKind.UserTurn
            || source.DefinitionId != child.DefinitionId || source.DefinitionVersion != child.DefinitionVersion
            || source.PinnedPersona != child.PinnedPersona
            || source.PinnedModel.CatalogKey != child.PinnedModel.CatalogKey
            || source.PinnedModel.ProviderAlias != child.PinnedModel.ProviderAlias
            || source.PinnedModel.ModelId != child.PinnedModel.ModelId
            || source.PinnedModel.ReasoningEffort != child.PinnedModel.ReasoningEffort)
            throw AgentCoreErrors.Validation("Immediate background admission requires an active owned user-turn source.");
    }

    public static Exception Map(Exception exception) => exception switch
    {
        AgentRunTransitionException e when e.Failure is AgentRunTransitionFailure.StaleRevision
            or AgentRunTransitionFailure.StaleGeneration or AgentRunTransitionFailure.NotClaimable
            => AgentCoreErrors.Conflict(e.Message),
        AgentRunTransitionException e => AgentCoreErrors.Validation(e.Message),
        ArgumentException => AgentCoreErrors.Validation("AgentRun transition is invalid."),
        _ => exception
    };
}
