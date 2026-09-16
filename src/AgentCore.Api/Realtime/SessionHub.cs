using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace AgentCore.Api.Realtime;

public sealed class SessionHub(SessionHost host) : Hub
{
    public Task<CommandAck> Attach(ClientCommand<AttachPayload> command)
    {
        command.Payload ??= new AttachPayload();
        if (string.IsNullOrEmpty(command.Payload.OwnerCapability))
        {
            command.Payload.OwnerCapability = Context.GetHttpContext()?
                .Request.Headers[OwnerCapabilityHeaders.Name]
                .ToString();
        }

        return Complete(host.AttachAsync(Context.ConnectionId, command, Context.ConnectionAborted));
    }

    public Task<CommandAck> SendText(ClientCommand<UserTextPayload> command) =>
        Complete(host.SendTextAsync(Context.ConnectionId, command, Context.ConnectionAborted));

    public Task<CommandAck> SetMode(ClientCommand<SetModePayload> command) =>
        Complete(host.SetModeAsync(Context.ConnectionId, command, Context.ConnectionAborted));

    public Task<CommandAck> EndSession(ClientCommand<EndPayload> command) =>
        Complete(host.EndAsync(Context.ConnectionId, command, Context.ConnectionAborted));

    public Task<CommandAck> SpeechStarted(ClientCommand<SpeechStartedPayload> command) =>
        Complete(host.AdmitSpeechStartedAsync(Context.ConnectionId, command));

    public Task<CommandAck> SpeechEnded(ClientCommand<SpeechEndedPayload> command) =>
        Complete(host.AdmitSpeechEndedAsync(Context.ConnectionId, command));

    public async Task SendAudio(InputAudioDto dto)
    {
        if (await host.AdmitAudioAsync(Context.ConnectionId, dto).ConfigureAwait(false))
        {
            Context.Abort();
        }
    }

    public Task<CommandAck> PlaybackStarted(ClientCommand<PlaybackPayload> command) =>
        Complete(host.AcceptPlaybackAsync(Context.ConnectionId, command, "playback.started"));

    public Task<CommandAck> PlaybackProgress(ClientCommand<PlaybackPayload> command) =>
        Complete(host.AcceptPlaybackAsync(Context.ConnectionId, command, "playback.progress"));

    public Task<CommandAck> PlaybackCompleted(ClientCommand<PlaybackPayload> command) =>
        Complete(host.AcceptPlaybackAsync(Context.ConnectionId, command, "playback.completed"));

    public Task<CommandAck> PlaybackStopped(ClientCommand<PlaybackPayload> command) =>
        Complete(host.AcceptPlaybackAsync(Context.ConnectionId, command, "playback.stopped"));

    public Task<CommandAck> ResponseReceived(ClientCommand<ResponseReceiptPayload> command) =>
        Complete(host.AcceptReceiptAsync(Context.ConnectionId, command));

    public Task<CommandAck> SetMuted(ClientCommand<MutePayload> command) =>
        Complete(host.AcceptMuteAsync(Context.ConnectionId, command));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
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
