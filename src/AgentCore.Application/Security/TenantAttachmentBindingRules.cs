using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Trusted source witnesses belong to the existing human message admission, not a separate bind command.
public static class TenantAttachmentBindingRules
{
    public static TenantHumanRunAdmission Freeze(TenantHumanRunAdmission value) => value with {
        Input = value.Input with { Attachments = value.Input.Attachments?.ToArray() }, Attachments = value.Attachments?.ToArray() };

    public static void Shape(TenantHumanRunAdmission value)
    {
        var sources = value.Attachments ?? []; var refs = value.Input.Attachments ?? [];
        if (sources.Count > AttachmentLimits.MaxPerMessage || sources.Count != refs.Count
            || sources.Select(s => s.AttachmentId).Distinct().Count() != sources.Count
            || sources.Select((s, i) => s.TenantId != value.Run.Owner.TenantId || s.SessionId != value.Run.SessionId
                || s.AttachmentId == Guid.Empty || refs[i] != new ConversationAttachmentRef(s.AttachmentId, s.DisplayName, s.ContentType)).Any(b => b))
            throw new ArgumentException("Message attachments require exact owned source witnesses.");
    }
    public static void Current(TenantHumanRunAdmission admission, IReadOnlyList<AttachmentRecord> actual, DateTimeOffset now)
    {
        Shape(admission); var sources = admission.Attachments ?? [];
        if (!sources.SequenceEqual(actual) || actual.Any(a => a.State != AttachmentState.Pending || a.EntryId is not null
            || a.StageForNextTurn || !TenantAttachmentRules.Available(a, now)))
            throw new AccessDeniedException("attachment_unavailable");
    }
    public static async ValueTask PermissionsAsync(TenantSecurityState state, Guid human, Guid session,
        IReadOnlyList<AttachmentRecord> sources, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var source in sources)
            await TenantAttachmentRules.PermissionsAsync(state, human, session, source.AttachmentId, "attachments.read", iam, ct).ConfigureAwait(false);
    }
    public static AttachmentRecord Bound(AttachmentRecord source, ConversationEntry input) => source with {
        State = AttachmentState.Bound, EntryId = input.EntryId, ExpiresAt = null, BoundAt = input.CreatedAt, StageForNextTurn = false };
}
