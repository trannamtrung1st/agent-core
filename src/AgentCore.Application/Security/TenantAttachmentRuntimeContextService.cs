using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantAttachmentRuntimeContextService(TenantOperationAuthorizer operations, ITenantSessionStore sessions,
    ITenantAttachmentStore files, TenantRunConfigurationResolver configurations, IamAuthorizer iam, IAttachmentProcessor processor, TimeProvider time, ITenantApprovalStore approvals, ITenantApprovedOperationStore effects, IIdGenerator ids, IModelCatalog models, IOfficeProcessor officeReader)
{
    private sealed record View(AgentRun Run, TenantSecurityState State, TenantSession Session, TenantAgent Agent);
    private async ValueTask<View> Load(TenantRuntimeFrame frame, CancellationToken ct, bool live = true)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (run.SessionId != frame.SessionId || session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId)
            throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        var current = await configurations.ResolveAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        TenantAttachmentRules.Session(session.Owner, session, current.Agent, true);
        if (run.Owner != frame.Owner || run.Admission.OutputContract is not (AgentRunOutputContract.ConversationResponse or AgentRunOutputContract.BackgroundOutcome or AgentRunOutputContract.CompletionReport)
            || run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation or ExecutionOrigin.ImmediateBackground or ExecutionOrigin.CompletionReport or ExecutionOrigin.ConfigurationContinuation)
                && !AgentCore.Application.Tools.TenantTaskExecutionContract.IsIndependent(run)) throw new AccessDeniedException("resource_unavailable");
        return new(run, state, session, current.Agent);
    }
    private async ValueTask<bool> Allowed(View view, Guid attachment, CancellationToken ct, bool audit)
    {
        foreach (var (permission, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(view.State.Tenant.TenantId, view.Run.SessionId)),
            ("attachments.read", TenantAttachmentRules.Scope(view.State.Tenant.TenantId, attachment)) })
        {
            if (!(await iam.CheckTenantAsync(view.State, view.Agent.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed) return false;
            var decision = audit ? await operations.Check(view.State, view.Run, permission, scope, ct, purpose: ExecutionAuthorizationPurpose.Inspection).ConfigureAwait(false)
                : await operations.Evaluate(view.State, view.Run.Admission.ExecutionContext!, permission, scope, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.Allowed) return false;
        }
        return true;
    }
    private async ValueTask Fence(TenantRuntimeFrame frame, View before, CancellationToken ct, bool live = true)
    {
        var after = await Load(frame, ct, live).ConfigureAwait(false);
        // The owning Runtime saves assistant progress concurrently with read-only prompt assembly.
        // Conversation revision is not source authority: current ownership, lifecycle, IAM/policy,
        // claim and immutable bound source checks remain mandatory on both sides of that save.
        if (after.Run.Revision != before.Run.Revision || after.State.Tenant.Revision != before.State.Tenant.Revision || after.Session.Owner != before.Session.Owner
            || after.Session.Snapshot.Mode != before.Session.Snapshot.Mode
            || after.Session.Snapshot.LifecycleStatus != before.Session.Snapshot.LifecycleStatus
            || after.Session.Snapshot.ArchivedAt != before.Session.Snapshot.ArchivedAt
            || after.Session.Snapshot.DurablyDeletedAt != before.Session.Snapshot.DurablyDeletedAt
            || after.Agent.Instance.Revision != before.Agent.Instance.Revision)
            throw new SecurityConflictException("attachment_context_changed");
    }
    private static TenantRuntimeFrame Frame(AgentRun run) => new(run.Owner, run.SessionId, run.AgentRunId,
        run.Claim?.Generation ?? throw new AccessDeniedException("tenant_runtime_unavailable"));

    public async ValueTask<TenantAttachmentContext> PrepareAsync(AgentRun run, SessionSnapshot snapshot, bool vision, CancellationToken ct = default, bool processCurrentContents = true)
    {
        var frame = Frame(run); var view = await Load(frame, ct).ConfigureAwait(false);
        if (snapshot.SessionId != frame.SessionId || snapshot.AgentInstanceId != frame.Owner.AgentInstanceId || snapshot.ProfileId is not null)
            throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        var current = view.Run.Admission.Activation.SourceEntryIds.ToHashSet();
        var candidates = snapshot.Entries.Where(e => e.Role == ConversationRole.User && e.Attachments is { Count: > 0 })
            .OrderByDescending(e => current.Contains(e.EntryId)).ThenByDescending(e => e.Sequence)
            .SelectMany(entry => entry.Attachments!.Select(reference => (Entry: entry, Ref: reference))).DistinctBy(item => item.Ref.AttachmentId);
        var sources = new List<TenantAttachmentContextSource>(); var contents = new List<AttachmentProcessResult>(); var manifest = new List<SessionAttachmentManifestItem>();
        var visible = new Dictionary<Guid, ConversationAttachmentRef>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested(); if (sources.Count >= TenantAttachmentContextRules.MaxSources) break;
            if (!await Allowed(view, candidate.Ref.AttachmentId, ct, audit: true).ConfigureAwait(false)) continue;
            var source = await files.FindTenantAsync(frame.Owner.TenantId!.Value, frame.SessionId, candidate.Ref.AttachmentId, ct).ConfigureAwait(false);
            if (source is null || source.State != AttachmentState.Bound || source.EntryId != candidate.Entry.EntryId) continue;
            string? version = null;
            if (processCurrentContents && current.Contains(candidate.Entry.EntryId))
            {
                if (!source.Readable || AttachmentMedia.IsImage(source.ContentType) && !vision)
                    contents.Add(new(source.AttachmentId, processor.Version, AttachmentProcessKind.Unsupported, source.DisplayName, source.ContentType,
                        source.Readable ? "This model cannot inspect image content. Select a vision-capable model." : "This format has no automatic content reader. Original bytes remain available through independently authorized download.",
                        null, null, source.Readable ? "vision_required" : "unsupported_reader"));
                else
                {
                    var bytes = await files.ReadTenantContentAsync(frame.Owner.TenantId.Value, frame.SessionId, source.AttachmentId, ct).ConfigureAwait(false);
                    TenantAttachmentRules.Content(source, bytes);
                    var result = await processor.ProcessSourceAsync(source, bytes, ct).ConfigureAwait(false);
                    if (result.AttachmentId != source.AttachmentId || result.DisplayName != source.DisplayName || result.ProcessorVersion != processor.Version)
                        throw new AccessDeniedException("attachment_source_changed");
                    contents.Add(result); version = processor.Version;
                }
            }
            if (await files.FindTenantAsync(frame.Owner.TenantId.Value, frame.SessionId, source.AttachmentId, ct).ConfigureAwait(false) != source)
                throw new AccessDeniedException("attachment_source_changed");
            sources.Add(TenantAttachmentContextRules.Witness(source, version));
            visible.Add(source.AttachmentId, new(source.AttachmentId, source.DisplayName, source.ContentType));
            manifest.Add(new(source.AttachmentId, source.DisplayName, source.ContentType, candidate.Entry.Sequence));
        }
        await Fence(frame, view, ct).ConfigureAwait(false);
        await RequireSourcesAsync(view.Run, sources, ct).ConfigureAwait(false);
        // These IDs came from this human's admitted input, not an Agent metadata read. Preserve only
        // current submitted selectors as untrusted user text; historical discovery stays permission-gated.
        var history = snapshot.Entries.Select(entry => {
            if (entry.Attachments is not { Count: > 0 }) return entry;
            var hidden = entry.Role == ConversationRole.User ? entry.Attachments.Where(a => !visible.ContainsKey(a.AttachmentId)).ToArray() : [];
            var text = entry.Text;
            if (entry.Role == ConversationRole.User && hidden.Length == entry.Attachments.Count)
                text += "\n[Attached files are unavailable under current access or read policy.]";
            if (current.Contains(entry.EntryId) && hidden.Length > 0)
                text += "\n[User-provided attachment references (identifiers only; access has not been established): "
                    + string.Join(", ", hidden.Select(a => a.AttachmentId.ToString("D"))) + "]";
            return entry with { Attachments = entry.Role == ConversationRole.User
                ? entry.Attachments.Where(a => visible.ContainsKey(a.AttachmentId)).Select(a => visible[a.AttachmentId]).ToArray() : null, Text = text };
        }).ToArray();
        return new(history, manifest.ToArray(), contents.ToArray(), TenantAttachmentContextRules.Merge(null, sources));
    }
    public async ValueTask RequireSourcesAsync(AgentRun run, IReadOnlyList<TenantAttachmentContextSource>? sources, CancellationToken ct = default)
    {
        var witnesses = TenantAttachmentContextRules.Merge(null, sources); if (witnesses.Count == 0) return;
        var frame = Frame(run); var view = await Load(frame, ct).ConfigureAwait(false);
        foreach (var witness in witnesses)
        {
            if (!await Allowed(view, witness.AttachmentId, ct, audit: false).ConfigureAwait(false)) throw new AccessDeniedException("attachment_read_unavailable");
            var item = await files.FindTenantAsync(frame.Owner.TenantId!.Value, frame.SessionId, witness.AttachmentId, ct).ConfigureAwait(false);
            if (item is not { State: AttachmentState.Bound } || TenantAttachmentContextRules.Witness(item, witness.ProcessorVersion) != witness
                || witness.ProcessorVersion is not null && witness.ProcessorVersion != processor.Version) throw new AccessDeniedException("attachment_source_changed");
            if (witness.ProcessorVersion is not null)
                TenantAttachmentRules.Content(item, await files.ReadTenantContentAsync(frame.Owner.TenantId.Value, frame.SessionId, item.AttachmentId, ct).ConfigureAwait(false));
        }
        await Fence(frame, view, ct).ConfigureAwait(false);
    }
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default)
    {
        var sources = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantAttachmentSources;
        await RequireSourcesAsync(run, sources, ct).ConfigureAwait(false);
        if (run.Checkpoint is not null && AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages))
            await RequireReadResultsAsync(run, messages!, ct).ConfigureAwait(false);
    }
}
