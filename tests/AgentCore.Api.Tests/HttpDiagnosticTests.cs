using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

internal sealed class SaveFailureApiFactory(Exception failure) : AgentCoreApiFactory
{
    private readonly DiagnosticLineLoggerProvider _logs = new();

    public IReadOnlyList<string> Logs => _logs.Lines;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var original = services.Single(item => item.ServiceType == typeof(IMemoryStore));
            services.Remove(original);
            services.AddSingleton<IMemoryStore>(provider =>
            {
                var inner = original.ImplementationFactory is not null
                    ? (IMemoryStore)original.ImplementationFactory(provider)
                    : (IMemoryStore)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
                return new SaveFailureStore(inner, failure);
            });
        });
        builder.ConfigureLogging(logging => logging.AddProvider(_logs));
    }
}

internal sealed class SaveFailureStore(IMemoryStore inner, Exception failure) : IMemoryStore
{
    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        inner.LoadAsync(sessionId, cancellationToken);

    public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default) =>
        throw failure;

    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        inner.LoadProfileAsync(profileId, cancellationToken);

    public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
        inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
        inner.RecoverCrashedSessionsAsync(cancellationToken);
}
