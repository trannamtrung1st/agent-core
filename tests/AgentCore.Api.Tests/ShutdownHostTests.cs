using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

internal sealed class HungDisposeApiFactory : AgentCoreApiFactory
{
    private readonly TaskCompletionSource _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseDispose() => _hold.TrySetResult();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISpeechRecognizer>();
            services.AddSingleton<ISpeechRecognizer>(new HungDisposeRecognizer(_hold.Task));
        });
    }
}

internal sealed class HungDisposeRecognizer(Task hold) : ISpeechRecognizer
{
    public RecognitionCapabilities Capabilities { get; } = new(true, true, true, true);

    public ValueTask<ISpeechRecognitionSession> OpenAsync(
        RecognitionOptions options,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ISpeechRecognitionSession>(new HungDisposeSession(hold));

    private sealed class HungDisposeSession(Task hold) : ISpeechRecognitionSession
    {
        public ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ObserveBoundaryAsync(
            Guid utteranceId,
            SpeechBoundary boundary,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteInputAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async ValueTask DisposeAsync() => await hold.ConfigureAwait(false);
    }
}
