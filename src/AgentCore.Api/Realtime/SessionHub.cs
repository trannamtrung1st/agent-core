using AgentCore.Application.Ports;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace AgentCore.Api.Realtime;

public sealed class SessionHub(SessionHost host) : Hub
{
    public Task<CommandAck> Attach(ClientCommand command) =>
        host.AttachAsync(Context.ConnectionId, command, Context.ConnectionAborted);

    public Task<CommandAck> SendText(ClientCommand command) =>
        host.SendTextAsync(Context.ConnectionId, command, Context.ConnectionAborted);

    public Task<CommandAck> SetMode(ClientCommand command) =>
        host.SetModeAsync(Context.ConnectionId, command, Context.ConnectionAborted);

    public Task<CommandAck> EndSession(ClientCommand command) =>
        host.EndAsync(Context.ConnectionId, command, Context.ConnectionAborted);

    public Task<CommandAck> SpeechStarted(ClientCommand command) =>
        host.AdmitSpeechAsync(Context.ConnectionId, command, SpeechBoundary.Started);

    public Task<CommandAck> SpeechEnded(ClientCommand command) =>
        host.AdmitSpeechAsync(Context.ConnectionId, command, SpeechBoundary.Ended);

    public Task SendAudio(InputAudioDto dto) => host.AdmitAudioAsync(Context.ConnectionId, dto);

    public Task<CommandAck> PlaybackStarted(ClientCommand command) => host.AcceptControlAsync(Context.ConnectionId, command);

    public Task<CommandAck> PlaybackProgress(ClientCommand command) => host.AcceptControlAsync(Context.ConnectionId, command);

    public Task<CommandAck> PlaybackCompleted(ClientCommand command) => host.AcceptControlAsync(Context.ConnectionId, command);

    public Task<CommandAck> PlaybackStopped(ClientCommand command) => host.AcceptControlAsync(Context.ConnectionId, command);

    public Task<CommandAck> ResponseReceived(ClientCommand command) => host.AcceptControlAsync(Context.ConnectionId, command);

    public Task<CommandAck> SetMuted(ClientCommand command) => host.AcceptControlAsync(Context.ConnectionId, command);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await host.DetachAsync(Context.ConnectionId).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
