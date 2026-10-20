using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// A new human message has its own root. The stopped response is an exact effect target,
// never a parent whose authority or approval lifetime the new message inherits.
public static class TenantInputInterruptionRules
{
    public static TenantInputInterruption? Request(string? behavior, Guid? runId, long? revision)
    {
        if (behavior is null or "queue" && runId is null && revision is null) return null;
        if (behavior == "interrupt" && runId is { } id && id != Guid.Empty && revision is >= 1) return new(id, revision.Value);
        throw AgentCore.Application.Sessions.AgentCoreErrors.Validation("Use queue, or interrupt with the exact current Run and revision.");
    }

    public static string? Evidence(TenantInputInterruption? value) => value is null ? null : JsonSerializer.Serialize(new {
        inputBehavior = "interrupt", interruptedRunId = value.RunId, interruptedRunRevision = value.Revision });

    public static void Shape(TenantHumanRunAdmission admission)
    {
        if (admission.Interruption is { } value && (value.RunId == Guid.Empty || value.Revision < 1 || value.RunId == admission.Run.AgentRunId)
            || admission.Run.Admission.Activation.EvidenceJson != Evidence(admission.Interruption))
            throw new ArgumentException("Human input must preserve its exact interruption intent.");
    }

    public static async ValueTask PermissionsAsync(TenantSecurityState state, Guid human, TenantInputInterruption? value, IamAuthorizer iam, CancellationToken ct)
    {
        if (value is null) return;
        foreach (var key in new[] { "agent-runs.read", "agent-runs.cancel" })
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), TenantRunRules.Scope(state.Tenant.TenantId, value.RunId), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("interruption_unavailable");
    }

    public static AgentRun Target(TenantHumanRunAdmission admission, AgentRun? source, TenantRuntimeFrame? frame, DateTimeOffset at)
    {
        var value = admission.Interruption ?? throw new ArgumentException("An exact interruption is required.");
        if (source is null || source.Owner != admission.Run.Owner || source.SessionId != admission.Run.SessionId)
            throw new AccessDeniedException("resource_unavailable");
        if (source.AgentRunId != value.RunId || source.Revision != value.Revision || source.Status != AgentRunStatus.Running || source.CancellationRequested
            || source.Claim is not { } claim || frame is null || frame.RunId != source.AgentRunId || frame.Generation != claim.Generation || claim.LeaseExpiresAtUtc <= at)
            throw new SecurityConflictException("stale_interruption");
        return TenantRunCancellationRules.Cancel(source, at);
    }

    public static SessionSnapshot Interrupt(SessionSnapshot snapshot, AgentRun source, DateTimeOffset at) => snapshot with {
        UpdatedAt = snapshot.UpdatedAt > at ? snapshot.UpdatedAt : at,
        Entries = snapshot.Entries.Select(e => TenantRunCancellationRules.Interrupt(e, source, at) is { Status: EntryStatus.Interrupted } changed
            && e.Role == ConversationRole.Assistant && e.ResponseId == source.ResponseId ? changed with { InterruptReason = "userSteer" } : e).ToArray() };

    public static AuthorizationAudit Audit(TenantHumanRunAdmission admission, TenantSessionInitiator human, DateTimeOffset at) => new(
        admission.Run.ActivationId, admission.Run.Owner.TenantId!.Value, human.PrincipalId, "agent-runs.cancel", admission.Interruption!.RunId.ToString("D"),
        "allowed", "human_message_interrupted", at);
}
