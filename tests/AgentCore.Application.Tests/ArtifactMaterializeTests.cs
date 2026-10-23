using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Application.Tests;

public sealed class ArtifactMaterializeTests
{

    [Fact]
    public async Task Concurrent_artifact_writes_respect_session_quota()
    {
        var session = Guid.CreateVersion7();
        var artifacts = new InMemoryArtifactStore(TimeProvider.System, maxBytesEach: 40, maxBytesSession: 50);
        var first = artifacts.CreateAsync(session, "a.bin", "application/octet-stream", new byte[40], null, null);
        var second = artifacts.CreateAsync(session, "b.bin", "application/octet-stream", new byte[40], null, null);
        var results = await Task.WhenAll(Capture(first), Capture(second));
        Assert.Contains(results, result => result is null);
        Assert.Contains(results, result => result is { Code: "ArtifactQuotaExceeded" });
    }

    private static async Task<AgentCoreException?> Capture(ValueTask<ArtifactRecord> task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (AgentCoreException ex)
        {
            return ex;
        }
    }

}
