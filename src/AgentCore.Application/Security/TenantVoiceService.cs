using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Speech;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantVoiceService(IIamStore security, ITenantSessionStore sessions, ITenantAgentStore agents,
    ITenantVoiceModeStore modes, IdentityService identity, IamAuthorizer iam, VoiceAvailability voice, IIdGenerator ids)
{
    public ValueTask<TenantVoiceLease> PrepareAsync(Guid tenant, Guid sessionId, TenantSessionInitiator human, Guid attachmentId, CancellationToken ct)
        => PrepareCoreAsync(tenant, sessionId, human, attachmentId, true, ct);
    public ValueTask<TenantVoiceLease> PrepareRetirementAsync(Guid tenant, Guid sessionId, TenantSessionInitiator human, Guid attachmentId, CancellationToken ct)
        => PrepareCoreAsync(tenant, sessionId, human, attachmentId, false, ct);
    private async ValueTask<TenantVoiceLease> PrepareCoreAsync(Guid tenant, Guid sessionId, TenantSessionInitiator human, Guid attachmentId, bool requireVoice, CancellationToken ct)
    {
        if (attachmentId == Guid.Empty) throw new AccessDeniedException("voice_lease_unavailable");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = await agents.FindTenantAgentAsync(tenant, saved.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantVoiceRules.RequireAsync(saved, agent, state, human, iam, ct).ConfigureAwait(false);
        if (requireVoice && !voice.IsAvailable(saved.Snapshot.Definition, SpeechLocale.Resolve(saved.Snapshot).Effective)) throw AgentCoreErrors.VoiceUnavailable();
        return new(saved.Owner, human, attachmentId, ids.NewId());
    }
    public async ValueTask<TenantSession> ActivateQuiescentAsync(TenantVoiceLease lease, CancellationToken ct)
    {
        await using var operation = await lease.EnterAsync(requireActive: false, ct).ConfigureAwait(false);
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, lease.Owner.TenantId, lease.Human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(lease.Owner.TenantId, lease.Owner.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = await agents.FindTenantAgentAsync(lease.Owner.TenantId, lease.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (saved.Owner != lease.Owner) throw new AccessDeniedException("voice_lease_unavailable");
        await TenantVoiceRules.RequireAsync(saved, agent, state, lease.Human, iam, ct).ConfigureAwait(false);
        if (!voice.IsAvailable(saved.Snapshot.Definition, SpeechLocale.Resolve(saved.Snapshot).Effective)) throw AgentCoreErrors.VoiceUnavailable();
        var result = await modes.ActivateTenantVoiceAsync(new(lease, operation, state.Tenant.Revision, saved.Snapshot.Revision, agent.Instance.Revision), ids.NewId(), ct).ConfigureAwait(false);
        lease.Activate(operation); return result;
    }
    public async ValueTask PauseInputAsync(TenantVoiceLease lease, CancellationToken ct)
    {
        await using var operation = await lease.EnterAsync(requireActive: true, ct).ConfigureAwait(false);
        await RequireCurrentAsync(lease, ct).ConfigureAwait(false); lease.PauseInput(operation);
    }
    public async ValueTask<Guid> ResumeInputAsync(TenantVoiceLease lease, CancellationToken ct)
    {
        await using var operation = await lease.EnterAsync(requireActive: true, ct).ConfigureAwait(false);
        await RequireCurrentAsync(lease, ct).ConfigureAwait(false);
        var stream = ids.NewId(); lease.ResumeInput(operation, stream); return stream;
    }
    private async ValueTask RequireCurrentAsync(TenantVoiceLease lease, CancellationToken ct)
    {
        // Only rebuild a conflicted read witness. Mutation occurs after this returns,
        // under the existing lease operation; denial and cancellation are never retried.
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { await RequireCurrentOnceAsync(lease, ct).ConfigureAwait(false); return; }
            catch (SecurityConflictException ex) when (ex.ReasonCode == "stale_revision" && attempt < 2) { }
        }
    }
    private async ValueTask RequireCurrentOnceAsync(TenantVoiceLease lease, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, lease.Owner.TenantId, lease.Human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(lease.Owner.TenantId, lease.Owner.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = await agents.FindTenantAgentAsync(lease.Owner.TenantId, lease.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (lease.Retiring || saved.Owner != lease.Owner || saved.Snapshot.Mode != SessionMode.Voice) throw new AccessDeniedException("voice_lease_unavailable");
        await TenantVoiceRules.RequireAsync(saved, agent, state, lease.Human, iam, ct).ConfigureAwait(false);
        var after = await TenantHumanSessionAuthority.RequireAsync(security, identity, lease.Owner.TenantId, lease.Human, ct).ConfigureAwait(false);
        if (after.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
    }
}

public static class TenantVoiceRules
{
    public static async ValueTask RequireAsync(TenantSession saved, TenantAgent agent, TenantSecurityState state, TenantSessionInitiator human, IamAuthorizer iam, CancellationToken ct)
    {
        var owner = saved.Owner;
        if (state.Tenant.TenantId != owner.TenantId || agent.TenantId != owner.TenantId || agent.PrincipalId != owner.ExecutorPrincipalId
            || agent.Instance.InstanceId != owner.AgentInstanceId) throw new AccessDeniedException("resource_unavailable");
        foreach (var (permission, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(owner.TenantId, owner.SessionId)),
            ("agents.chat", AccessScope.ForResource(owner.TenantId, "agent", owner.AgentInstanceId.ToString("D"))) })
            if (!(await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
        TenantSessionRules.RequireExecutor(state, agent);
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        TenantHumanInputRules.Eligible(saved.Snapshot);
    }
    public static void Shape(TenantVoiceModeChange request, TenantSession saved, TenantAgent agent, TenantSecurityState state)
    {
        if (!request.Operation.IsHeldBy(request.Lease) || saved.Owner != request.Lease.Owner || request.Lease.Active)
            throw new AccessDeniedException("voice_lease_unavailable");
        if (request.SecurityRevision != state.Tenant.Revision || request.ObservedSessionRevision != saved.Snapshot.Revision || request.InstanceRevision != agent.Instance.Revision)
            throw new SecurityConflictException("stale_revision");
    }
    public static void Speech(TenantHumanRunAdmission admission, TenantSessionInitiator human, TenantSessionRoot owner, SessionMode mode, TenantVoiceLease.Operation? operation)
    {
        if (admission.Speech is not { } speech)
        {
            if (admission.Input.DeliveryMode != SessionMode.Text) throw new AccessDeniedException("voice_lease_unavailable");
            return;
        }
        if (mode != SessionMode.Voice || speech.Lease.Owner != owner || speech.Lease.Human != human || speech.UtteranceId == Guid.Empty || speech.UtteranceId != admission.Input.SourceEventId
            || operation?.IsHeldInputBy(speech.Lease) != true || operation.StreamId != speech.StreamId
            || admission.Input.DeliveryMode != SessionMode.Voice || admission.Attachments is { Count: > 0 })
            throw new AccessDeniedException("voice_lease_unavailable");
    }
}
