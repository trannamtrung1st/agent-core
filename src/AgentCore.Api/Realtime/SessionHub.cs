using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using AgentCore.Api.Security;
using AgentCore.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AgentCore.Api.Realtime;

public sealed class SessionHub(SessionHost host, TenantSessionLiveDelivery tenants, IdentityService identity, IdentityHttpOptions policy, TimeProvider time,
    TenantRunService runs, IamAuthorizer authorizer, ITenantDeliveryReceiptControl receipts, ITenantRunDispatcher tenantDispatcher) : Hub

{
    private TenantSessionRuntimeHost TenantRuntime => (TenantSessionRuntimeHost)tenantDispatcher;
    [Authorize]
    public async Task<CommandAck> AttachTenant(ClientCommand<TenantAttachPayload> command)
    {
        var validation = SessionHost.Validate(command, attach: true, expectedType: "session.attach");
        if (!validation.Accepted) return validation;
        try
        {
            var http = Context.GetHttpContext() ?? throw new AccessDeniedException("tenant_connection_unavailable");
            var origin = http.Request.Headers.Origin;
            if (!http.Request.IsHttps && (!policy.AllowInsecureLocalHttp || !Http.TrustedLocalCaller.IsLoopback(http)))
                throw new AccessDeniedException("https_required");
            if (origin.Count != 1 || string.IsNullOrWhiteSpace(origin[0])
                || !string.Equals(origin[0], $"{http.Request.Scheme}://{http.Request.Host}", StringComparison.OrdinalIgnoreCase)
                    && !policy.AllowedOrigins.Contains(origin[0], StringComparer.OrdinalIgnoreCase))
                throw new AccessDeniedException("origin_not_allowed");
            if (!Guid.TryParse(command.Payload?.TenantId, out var tenant) || tenant == Guid.Empty
                || IdentityHttp.AccountId(Context.User) is not { } account || IdentityHttp.SessionId(Context.User) is not { } authentication)
                throw new AccessDeniedException("initiator_unavailable");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted, deadline.Token);
            var user = await identity.ResolveUserAsync(account, tenant, bounded.Token).ConfigureAwait(false) ?? throw new AccessDeniedException("not_member");
            // A connection cannot remain in both authority registries after switching to tenant transport.
            await host.DetachAsync(Context.ConnectionId).ConfigureAwait(false);
            await TenantRuntime.ResetOrphanVoiceAsync(tenant, Guid.Parse(command.SessionId), new(account, authentication, user.PrincipalId), bounded.Token).ConfigureAwait(false);
            await tenants.AttachAsync(Context.ConnectionId, tenant, Guid.Parse(command.SessionId), new(account, authentication, user.PrincipalId), bounded.Token).ConfigureAwait(false);
            return validation;
        }
        catch (AccessDeniedException ex) { await tenants.DetachAsync(Context.ConnectionId).ConfigureAwait(false); return Denied(command.EventId, ex.ReasonCode); }
        catch (SecurityConflictException ex) { await tenants.DetachAsync(Context.ConnectionId).ConfigureAwait(false); return Denied(command.EventId, ex.ReasonCode); }
        catch (OperationCanceledException) { await tenants.DetachAsync(Context.ConnectionId).ConfigureAwait(false); return Denied(command.EventId, "tenant_connection_unavailable"); }
    }
    private static CommandAck Denied(string eventId, string reason) => new() { EventId = eventId, Accepted = false,
        Error = new() { Category = "Authorization", Code = reason, Message = "Tenant subscription is unavailable.", Fatal = false } };
    public async Task<CommandAck> Attach(ClientCommand<AttachPayload> command)
    {
        await tenants.DetachAsync(Context.ConnectionId).ConfigureAwait(false);
        command.Payload ??= new AttachPayload();
        if (string.IsNullOrEmpty(command.Payload.OwnerCapability))
        {
            command.Payload.OwnerCapability = Context.GetHttpContext()?
                .Request.Headers[OwnerCapabilityHeaders.Name]
                .ToString();
        }

        return await Complete(host.AttachAsync(Context.ConnectionId, command, Context.ConnectionAborted)).ConfigureAwait(false);
    }

    public Task<CommandAck> SendText(ClientCommand<UserTextPayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.SendTextAsync(Context.ConnectionId, command, runs, Context.ConnectionAborted)
            : host.SendTextAsync(Context.ConnectionId, command, Context.ConnectionAborted));

    public Task<CommandAck> CancelResponse(ClientCommand<CancelResponsePayload> command)
    {
        command.Payload ??= new CancelResponsePayload();
        return Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.CancelResponseAsync(Context.ConnectionId, command, runs, authorizer, Context.ConnectionAborted)
            : host.CancelResponseAsync(Context.ConnectionId, command, Context.ConnectionAborted));
    }

    public Task<CommandAck> RespondApproval(ClientCommand<ApprovalResponsePayload> command)
    {
        command.Payload ??= new ApprovalResponsePayload();
        return Complete(host.RespondApprovalAsync(Context.ConnectionId, command, Context.ConnectionAborted));
    }

    public Task<CommandAck> SetMode(ClientCommand<SetModePayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.SetVoiceModeAsync(Context.ConnectionId, command, TenantRuntime, Context.ConnectionAborted)
            : host.SetModeAsync(Context.ConnectionId, command, Context.ConnectionAborted));

    public Task<CommandAck> EndSession(ClientCommand<EndPayload> command) =>
        Complete(host.EndAsync(Context.ConnectionId, command, Context.ConnectionAborted));

    public Task<CommandAck> SpeechStarted(ClientCommand<SpeechStartedPayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.VoiceBoundaryAsync(Context.ConnectionId, command, TenantRuntime, true, Context.ConnectionAborted)
            : host.AdmitSpeechStartedAsync(Context.ConnectionId, command));

    public Task<CommandAck> SpeechEnded(ClientCommand<SpeechEndedPayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.VoiceBoundaryAsync(Context.ConnectionId, command, TenantRuntime, false, Context.ConnectionAborted)
            : host.AdmitSpeechEndedAsync(Context.ConnectionId, command));

    public Task<CommandAck> SpeechEvidence(ClientCommand<ClientSpeechEvidencePayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.VoiceEvidenceAsync(Context.ConnectionId, command, TenantRuntime, Context.ConnectionAborted)
            : host.AdmitSpeechEvidenceAsync(Context.ConnectionId, command, Context.ConnectionAborted));

    public async Task SendAudio(InputAudioDto dto)
    {
        if (await (tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.VoiceAudioAsync(Context.ConnectionId, dto, TenantRuntime, Context.ConnectionAborted)
            : host.AdmitAudioAsync(Context.ConnectionId, dto)).ConfigureAwait(false))
        {
            Context.Abort();
        }
    }

    public Task<CommandAck> PlaybackStarted(ClientCommand<PlaybackPayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.VoicePlaybackAsync(Context.ConnectionId, command, TenantRuntime, "started", Context.ConnectionAborted)
            : host.AcceptPlaybackAsync(Context.ConnectionId, command, "playback.started"));

    public Task<CommandAck> PlaybackProgress(ClientCommand<PlaybackPayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.VoicePlaybackAsync(Context.ConnectionId, command, TenantRuntime, "progress", Context.ConnectionAborted)
            : host.AcceptPlaybackAsync(Context.ConnectionId, command, "playback.progress"));

    public Task<CommandAck> PlaybackCompleted(ClientCommand<PlaybackPayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.VoicePlaybackAsync(Context.ConnectionId, command, TenantRuntime, "completed", Context.ConnectionAborted)
            : host.AcceptPlaybackAsync(Context.ConnectionId, command, "playback.completed"));

    public Task<CommandAck> PlaybackStopped(ClientCommand<PlaybackPayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.VoicePlaybackAsync(Context.ConnectionId, command, TenantRuntime, "stopped", Context.ConnectionAborted)
            : host.AcceptPlaybackAsync(Context.ConnectionId, command, "playback.stopped"));

    public Task<CommandAck> ResponseReceived(ClientCommand<ResponseReceiptPayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.ReceiveAsync(Context.ConnectionId, command, receipts, Context.ConnectionAborted)
            : host.AcceptReceiptAsync(Context.ConnectionId, command));

    public Task<CommandAck> SetMuted(ClientCommand<MutePayload> command) =>
        Complete(tenants.IsTenantConnection(Context.ConnectionId)
            ? tenants.MuteVoiceAsync(Context.ConnectionId, command, TenantRuntime, Context.ConnectionAborted)
            : host.AcceptMuteAsync(Context.ConnectionId, command));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await tenants.DetachAsync(Context.ConnectionId).ConfigureAwait(false);
        await host.DetachAsync(Context.ConnectionId).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    private async Task<CommandAck> Complete(Task<CommandAck> pending)
    {
        var ack = await pending.ConfigureAwait(false);
        if (ack.Error?.Fatal == true)
        {
            var http = Context.GetHttpContext();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    http?.Abort();
                }
                catch
                {
                    // ignored
                }
            }, CancellationToken.None);
        }

        return ack;
    }
}
