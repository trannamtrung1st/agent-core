using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantHumanInputReceipt(TenantRunReceipt Run, long EntrySequence);
public sealed record TenantRunReceipt(bool Created, Guid RunId, Guid EntryId, Guid ResponseId, Guid ActivationId, Guid RootActivationId, long SessionRevision, long SecurityRevision);
public sealed record TenantRunMetadata(TrustedExecutionContext Context, Guid AgentInstanceId, AgentRunStatus Status, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? ContinuationRunId = null, AgentRunOutcomeKind? OutcomeKind = null, bool CanCancel = false);

public sealed class TenantRunService(IIamStore security, ITenantSessionStore sessions, ITenantRunStore runs,
    TenantRunConfigurationResolver configurations, IdentityService identity, IamAuthorizer authorizer, IModelCatalog models, IIdGenerator ids, TimeProvider time, TenantRunCancellationService? cancellations = null, ITenantRunControl? control = null, ITenantAttachmentStore? attachments = null, ITenantSessionInputControl? inputs = null,
    ITenantRequestIsolationStore? requestIsolation = null)
{
    public async ValueTask<TenantRunReceipt> SubmitTextAsync(Guid tenant, Guid sessionId, TenantSessionInitiator initiator,
        Guid messageId, string text, long expectedSecurityRevision, long expectedSessionRevision, CancellationToken ct = default)
        => await SubmitMessageAsync(tenant, sessionId, initiator, messageId, text, [], expectedSecurityRevision, expectedSessionRevision, ct).ConfigureAwait(false);

    public async ValueTask<TenantRunReceipt> SubmitMessageAsync(Guid tenant, Guid sessionId, TenantSessionInitiator initiator,
        Guid messageId, string text, IReadOnlyList<Guid> attachmentIds, long expectedSecurityRevision, long expectedSessionRevision, CancellationToken ct = default,
        TenantInputInterruption? interruption = null)
        => (await SubmitInputAsync(tenant, sessionId, initiator, messageId, text, attachmentIds, expectedSecurityRevision, expectedSessionRevision, ct, interruption, null).ConfigureAwait(false)).Run;

    public async ValueTask<TenantHumanInputReceipt> SubmitSpeechAsync(TenantVoiceLease lease, Guid streamId, Guid utteranceId, string text, CancellationToken ct = default)
    {
        if (!lease.InputActive || streamId != lease.StreamId || utteranceId == Guid.Empty) throw new AccessDeniedException("voice_lease_unavailable");
        for (var attempt = 0; ; attempt++)
        {
            var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, lease.Owner.TenantId, lease.Human, ct).ConfigureAwait(false);
            var saved = await sessions.FindTenantSessionAsync(lease.Owner.TenantId, lease.Owner.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            try { return await SubmitInputAsync(lease.Owner.TenantId, lease.Owner.SessionId, lease.Human, utteranceId, text, [], state.Tenant.Revision, saved.Snapshot.Revision, ct, null, new(lease, streamId, utteranceId)).ConfigureAwait(false); }
            catch (SecurityConflictException ex) when (ex.ReasonCode == "stale_revision" && attempt < 2 && lease.InputActive && streamId == lease.StreamId) { }
        }
    }
    private async ValueTask<TenantHumanInputReceipt> SubmitInputAsync(Guid tenant, Guid sessionId, TenantSessionInitiator initiator,
        Guid messageId, string text, IReadOnlyList<Guid> attachmentIds, long expectedSecurityRevision, long expectedSessionRevision,
        CancellationToken ct, TenantInputInterruption? interruption, TenantSpeechAdmission? speech)
    {
        var requested = attachmentIds.ToArray();
        if (requested.Length > AttachmentLimits.MaxPerMessage || requested.Any(id => id == Guid.Empty) || requested.Distinct().Count() != requested.Length)
            throw AgentCoreErrors.Validation("Use at most ten distinct attachment IDs.");
        var session = await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        await Require(state, initiator.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, sessionId), ct).ConfigureAwait(false);
        await Require(state, initiator.PrincipalId, "agents.chat", AccessScope.ForResource(tenant, "agent", session.Owner.AgentInstanceId.ToString("D")), ct).ConfigureAwait(false);
        if (text is null || messageId == Guid.Empty || string.IsNullOrWhiteSpace(text) && requested.Length == 0 || text.Length > 8000) throw AgentCoreErrors.Validation("A message ID and text up to 8000 characters or an attachment are required.");
        var replay = await runs.FindTenantAcceptedHumanInputAsync(session.Owner, initiator.PrincipalId, messageId, text, requested, interruption, speech is null ? SessionMode.Text : SessionMode.Voice, ct).ConfigureAwait(false);
        if (replay is not null)
        {
            await RequireMessageInterruptionAsync(tenant, sessionId, initiator, interruption, ct).ConfigureAwait(false);
            await RequireMessageAttachmentsAsync(tenant, sessionId, initiator, requested, ct).ConfigureAwait(false);
            var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
            await Require(current, initiator.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, sessionId), ct).ConfigureAwait(false);
            await Require(current, initiator.PrincipalId, "agents.chat", AccessScope.ForResource(tenant, "agent", session.Owner.AgentInstanceId.ToString("D")), ct).ConfigureAwait(false);
            if (current.Tenant.Revision != state.Tenant.Revision || (await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false))?.Owner != session.Owner)
                throw new SecurityConflictException("stale_revision");
            if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false)).Tenant.Revision != state.Tenant.Revision)
                throw new SecurityConflictException("stale_revision");
            return new(new(false, replay.Run.AgentRunId, replay.EntryId, replay.Run.ResponseId!.Value, replay.Run.ActivationId,
                replay.Run.Admission.Activation.RootActivationId, replay.SessionRevision, current.Tenant.Revision), replay.EntrySequence);
        }
        if (requestIsolation is not null && await requestIsolation.HasTenantRequestBoundaryAsync(session.Owner, ct).ConfigureAwait(false)) throw RequestIsolation.FreshSessionRequired();
        if (state.Tenant.Revision != expectedSecurityRevision || session.Snapshot.Revision != expectedSessionRevision) throw new SecurityConflictException("stale_revision");
        var (agent, resolved) = await configurations.ResolveAsync(tenant, session.Owner.AgentInstanceId, ct).ConfigureAwait(false);
        if (agent.PrincipalId != session.Owner.ExecutorPrincipalId) throw new AccessDeniedException("executor_unavailable");
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        TenantSessionRules.RequireExecutor(state, agent);
        var sources = new List<AttachmentRecord>(requested.Length);
        foreach (var id in requested)
        {
            if (attachments is null) throw new AccessDeniedException("attachment_unavailable");
            var item = await attachments.FindTenantAsync(tenant, sessionId, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("attachment_unavailable");
            await TenantAttachmentBindingRules.PermissionsAsync(state, initiator.PrincipalId, sessionId, [item], authorizer, ct).ConfigureAwait(false);
            sources.Add(item);
        }
        if (speech is not null && (!speech.Lease.InputActive || speech.StreamId != speech.Lease.StreamId || speech.Lease.Owner != session.Owner || speech.Lease.Human != initiator
            || speech.UtteranceId != messageId || session.Snapshot.Mode != SessionMode.Voice || requested.Length != 0))
            throw new AccessDeniedException("voice_lease_unavailable");
        var now = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var activationId = ids.NewId(); var runId = ids.NewId(); var entryId = ids.NewId(); var responseId = ids.NewId();
        var input = new ConversationEntry(entryId, session.Snapshot.DurableLastEntrySequence + 1, messageId, ConversationRole.User,
            text, null, EntryStatus.Completed, speech is null ? SessionMode.Text : SessionMode.Voice, 0, text.Length, now,
            Attachments: sources.Count == 0 ? null : sources.Select(a => new ConversationAttachmentRef(a.AttachmentId, a.DisplayName, a.ContentType)).ToArray());
        var activation = Activation.Start(activationId, sessionId, ActivationKind.UserTurn, [entryId], messageId, null, null, null,
            $"human:{initiator.PrincipalId:D}:{messageId:D}", now, TenantInputInterruptionRules.Evidence(interruption));
        var context = new TrustedExecutionContext(tenant, agent.PrincipalId, initiator.PrincipalId, sessionId, runId, activationId, activationId, null, null, ExecutionOrigin.UserMessage);
        var definition = resolved.Configuration.Definition;
        var selected = SessionModelBinder.RefreshDefault(models, definition, session.Snapshot.ModelSelection)
            ?? throw AgentCoreErrors.Validation("A resolved model is required.");
        var run = AgentRun.Create(runId, AgentRunOwner.ForTenant(tenant, agent.Instance.InstanceId, agent.PrincipalId),
            new(activation, definition.Id, definition.Version, resolved.Persona, responseId, AgentRunOutputContract.ConversationResponse,
                ExecutionBudgetResolver.Resolve(definition, resolved.InstanceBudgets, false), resolved.Configuration, executionContext: context),
            new(selected.CatalogKey, selected.ProviderAlias, selected.ModelId, selected.ReasoningEffort), AgentRunLimits.DefaultMaxAttempts, now,
            resolved.Skills, resolved.Skills.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).ToArray());
        var admission = new TenantHumanRunAdmission(run, input, expectedSessionRevision, agent.Instance.Revision, sources.Count == 0 ? null : sources.ToArray(), interruption, speech);
        var prepared = new TenantHumanInput(session.Owner, initiator, state.Tenant.Revision, TenantAttachmentBindingRules.Freeze(admission));
        await TenantHumanInputRules.RequireAsync(prepared, session, agent, state, authorizer, live: inputs is not null, ct).ConfigureAwait(false);
        var committed = inputs is not null ? await inputs.SubmitAsync(prepared, ct).ConfigureAwait(false)
            : await TenantHumanInputRules.CommitQuiescentAsync(prepared, security, runs, sessions, ids, time, ct).ConfigureAwait(false);
        return new(TenantHumanInputRules.Receipt(committed), committed.Admission.EntrySequence);
    }
    // A cached transport acknowledgement is a fact about earlier admission, never a file-read token.
    public async ValueTask RequireMessageInterruptionAsync(Guid tenant, Guid sessionId, TenantSessionInitiator initiator,
        TenantInputInterruption? interruption, CancellationToken ct = default)
    {
        if (interruption is null) return;
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        var source = await runs.FindTenantRunAsync(tenant, interruption.RunId, ct).ConfigureAwait(false);
        if (source?.SessionId != sessionId) throw new AccessDeniedException("resource_unavailable");
        await TenantInputInterruptionRules.PermissionsAsync(state, initiator.PrincipalId, interruption, authorizer, ct).ConfigureAwait(false);
    }

    public async ValueTask RequireMessageAttachmentsAsync(Guid tenant, Guid sessionId, TenantSessionInitiator initiator,
        IReadOnlyList<Guid> attachmentIds, CancellationToken ct = default)
    {
        if (attachmentIds.Count > AttachmentLimits.MaxPerMessage || attachmentIds.Any(id => id == Guid.Empty) || attachmentIds.Distinct().Count() != attachmentIds.Count)
            throw new AccessDeniedException("attachment_unavailable");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        await Require(state, initiator.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, sessionId), ct).ConfigureAwait(false);
        foreach (var id in attachmentIds)
        {
            var source = attachments is null ? null : await attachments.FindTenantAsync(tenant, sessionId, id, ct).ConfigureAwait(false);
            if (source is null) throw new AccessDeniedException("attachment_unavailable");
            await TenantAttachmentBindingRules.PermissionsAsync(state, initiator.PrincipalId, sessionId, [source], authorizer, ct).ConfigureAwait(false);
        }
        var after = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        if (after.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<TenantRunMetadata> ReadAsync(Guid tenant, Guid runId, TenantSessionInitiator initiator, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        var run = await runs.FindTenantRunAsync(tenant, runId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, initiator.PrincipalId, "agent-runs.read", TenantRunRules.Scope(tenant, runId), ct).ConfigureAwait(false);
        await Require(state, initiator.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, run.SessionId), ct).ConfigureAwait(false);
        var canCancel = !run.IsTerminal && (await authorizer.CheckTenantAsync(state, initiator.PrincipalId, PermissionCatalog.Get("agent-runs.cancel"), TenantRunRules.Scope(tenant, runId), ct).ConfigureAwait(false)).Allowed;
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(run.Admission.ExecutionContext!, run.AgentInstanceId, run.Status, run.Revision, run.CreatedAtUtc, run.UpdatedAtUtc, run.Result?.ContinuationRunId, run.Result?.OutcomeKind, canCancel);
    }
    public async ValueTask<TenantRunMetadata> CancelAsync(Guid tenant, Guid runId, TenantSessionInitiator initiator, long expectedSecurityRevision, long expectedRunRevision, CancellationToken ct = default)
    {
        var service = cancellations ?? new TenantRunCancellationService(security, runs, identity, authorizer, ids, time);
        var request = await service.PrepareAsync(tenant, runId, initiator, expectedSecurityRevision, expectedRunRevision, ct).ConfigureAwait(false);
        var saved = control is null ? await service.CommitAsync(request, ct).ConfigureAwait(false)
            : await control.CancelAsync(request, ct).ConfigureAwait(false);
        return new(saved.Admission.ExecutionContext!, saved.AgentInstanceId, saved.Status, saved.Revision, saved.CreatedAtUtc, saved.UpdatedAtUtc);
    }
    private async ValueTask Require(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    { var result = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false); if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode); }
}
