using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public static class TenantSessionMutations
{
    public static IReadOnlyList<AgentCore.Domain.Security.RoleAssignment> RetireAssignments(IReadOnlyList<AgentCore.Domain.Security.RoleAssignment> assignments,
        TenantSessionRoot owner, IReadOnlySet<Guid> runs, IReadOnlySet<Guid> attachments, IReadOnlySet<Guid> artifacts) =>
        assignments.Where(a => a.Scope != TenantSessionRules.Scope(owner.TenantId, owner.SessionId)
            && !TenantWorkspaceRules.BelongsToSession(a.Scope, owner.SessionId)
            && !(TenantRunRules.TryId(a.Scope, out var run) && runs.Contains(run))
            && !(TenantAttachmentRules.TryId(a.Scope, out var attachment) && attachments.Contains(attachment))
            && !(TenantArtifactRules.TryId(a.Scope, out var artifact) && artifacts.Contains(artifact))).ToArray();
    public static SessionSnapshot ExpireDue(SessionSnapshot current, DateTimeOffset at)
    {
        if (current.DurablyDeletedAt is not null || !SessionRetention.IsDue(current, at)) throw new SecurityConflictException("retention_not_due");
        var settled = current with { PendingAgentInputIds = [], PendingMode = null,
            Status = SessionLifecycle.IsTerminal(current.LifecycleStatus) ? SessionStatus.Ended : SessionStatus.Created };
        if (!SessionLifecycle.IsTerminal(settled.LifecycleStatus))
            settled = LifecycleTransition.Apply(settled, SessionLifecycleStatus.Expired, LifecycleTransitionSource.System, at, "retention-expired");
        return settled;
    }
    public static SessionSnapshot RetireDue(SessionSnapshot current, DateTimeOffset at) =>
        Apply(ExpireDue(current, at), new(current.SessionId, current.Revision, TenantSessionAction.Delete), at);
    public static ConversationEntry RedactEntry(ConversationEntry entry) => entry with { Text = "", Parts = null, Envelope = null, Attachments = null,
        Failure = null, SourceAdmissionFingerprint = null, FinishReason = null, InterruptReason = null, ModelProvenance = null,
        HeardTextEndExclusive = 0, ReceivedTextEndExclusive = 0, Status = entry.Status == EntryStatus.Streaming ? EntryStatus.Interrupted : entry.Status };
    public static SessionSnapshot Apply(SessionSnapshot current, TenantSessionChange change, DateTimeOffset at)
    {
        if (current.SessionId != change.SessionId || current.DurablyDeletedAt is not null || !Enum.IsDefined(change.Action)) throw new ArgumentException("Invalid Session mutation.");
        if (current.Status is SessionStatus.Attached or SessionStatus.Ending || current.PendingAgentInputIds.Count > 0) throw new SecurityConflictException("session_in_use");
        if (change.Action != TenantSessionAction.Rename && change.Title is not null) throw AgentCoreErrors.Validation("Title applies only to renaming a Session.");
        if ((change.Action == TenantSessionAction.SelectModel) != (change.Model is not null))
            throw AgentCoreErrors.Validation("A model selection applies only to a model change.");
        if ((change.Action == TenantSessionAction.SetAgentCompletion) != (change.AgentCompletionAllowed is not null))
            throw AgentCoreErrors.Validation("Agent completion delegation applies only to its policy command.");
        if (change.Action == TenantSessionAction.SetAgentCompletion && (current.LifecycleStatus != SessionLifecycleStatus.Active || current.ArchivedAt is not null))
            throw new SecurityConflictException("session_in_use");
        if (change.Action == TenantSessionAction.SelectModel
            && (current.Mode != SessionMode.Text || current.PendingMode is not null || current.ArchivedAt is not null
                || current.LifecycleStatus != SessionLifecycleStatus.Active || !current.Surfaces.HasFlag(SessionSurface.ChatList)))
            throw new SecurityConflictException("session_in_use");
        var next = change.Action switch
        {
            TenantSessionAction.SetAgentCompletion => current with { CompletionPolicy = (current.CompletionPolicy ?? SessionCompletionPolicy.Default) with {
                AgentCompletion = change.AgentCompletionAllowed == true ? AgentCompletionAuthority.Allowed : AgentCompletionAuthority.Disabled } },
            TenantSessionAction.SelectModel => current with { ModelSelection = change.Model },
            TenantSessionAction.Rename => current with { Title = Title(change.Title) },
            TenantSessionAction.Archive => current with { ArchivedAt = current.ArchivedAt ?? at },
            TenantSessionAction.Restore => current with { ArchivedAt = null },
            TenantSessionAction.Pause => LifecycleTransition.Apply(current, SessionLifecycleStatus.Paused, LifecycleTransitionSource.User, at, "manual"),
            TenantSessionAction.Resume => current.ArchivedAt is not null ? throw AgentCoreErrors.SessionArchived()
                : LifecycleTransition.Apply(current, SessionLifecycleStatus.Active, LifecycleTransitionSource.User, at, "reopen"),
            TenantSessionAction.Complete => LifecycleTransition.Apply(current, SessionLifecycleStatus.Completed, LifecycleTransitionSource.User, at, "user-completed"),
            TenantSessionAction.Cancel => LifecycleTransition.Apply(current, SessionLifecycleStatus.Cancelled, LifecycleTransitionSource.User, at, "user-cancelled"),
            TenantSessionAction.Delete => current with { DurablyDeletedAt = at, Entries = [], Summary = "", SummarizedThroughEntrySequence = 0,
                SummaryFormatVersion = SummaryFormats.Legacy, SummaryGeneratedAt = null, SummaryModel = null, PendingTopic = null, PendingAgentInputIds = [], PendingMode = null },
            _ => throw AgentCoreErrors.Validation("Unknown Session action.")
        };
        return next with { Revision = checked(current.Revision + 1), UpdatedAt = at };
    }
    public static string Permission(TenantSessionAction action) => action == TenantSessionAction.Delete ? "sessions.delete" : "sessions.manage";
    public static string AuditAction(TenantSessionAction action) => "sessions." + action.ToString().ToLowerInvariant();
    private static string Title(string? value)
    {
        var title = value?.Trim() ?? "";
        if (title.Length is < 1 or > SessionTitles.MaxLength || title.Any(char.IsControl)) throw AgentCoreErrors.Validation("Title must contain 1–200 characters without control characters.");
        return title;
    }
}
