using System.Net;
using System.Net.Http.Json;
using AgentCore.Api;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

public sealed class BackgroundSessionJourneyTests
{
    [Fact(Timeout = 60000)]
    public async Task Owned_background_session_pages_runs_and_foregrounds_same_identity_without_admitting_a_turn()
    {
        await using var factory = new PausedDispatchHost();
        using var client = TestOwnerCapability.CreateOwnerClient(factory);
        var services = factory.Services;
        var instanceId = TestInstances.Create(client, "examiner", 1);
        var sessions = services.GetRequiredService<SessionManager>();
        var memory = services.GetRequiredService<IMemoryStore>();
        var runs = services.GetRequiredService<IAgentRunStore>();
        var parent = await sessions.CreateForInstanceAsync(instanceId, SessionMode.Text);
        var now = DateTimeOffset.UtcNow;
        var input = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User, "Start a background check", null,
            EntryStatus.Completed, SessionMode.Text, 0, 24, now);
        var proposedParent = parent with { Entries = [input], LastEntrySequence = 1 };
        var source = AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), proposedParent, [input], now, []);
        source = (await runs.AdmitAsync(proposedParent, parent.Revision, source)).Run;
        source = await runs.ApplyAsync(source.Owner, source.AgentRunId, new AgentRunCommand.Claim(source.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
        var child = BackgroundSessionAdmissionFactory.ForImmediate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            proposedParent, source, "http-background", "Check progress without blocking chat", "Progress check", true, now);
        await runs.AdmitImmediateAsync(child.Session, child.Run, source.Claim!.Generation);
        var backgroundPath = $"/api/v2/agent-instances/{instanceId}/background-sessions";
        var listed = await client.GetFromJsonAsync<BackgroundSessionPageResponse>(backgroundPath);
        var row = Assert.Single(listed!.Items);
        Assert.Equal(child.Session.SessionId.ToString(), row.Session.SessionId);
        Assert.DoesNotContain((await memory.ListCatalogAsync(null, 50, false)).Items, item => item.SessionId == child.Session.SessionId);
        var path = $"/api/v2/sessions/{child.Session.SessionId}";
        var first = await client.PostAsJsonAsync(path + "/continue-in-chat", new { }); first.EnsureSuccessStatusCode();
        var second = await client.PostAsJsonAsync(path + "/continue-in-chat", new { }); second.EnsureSuccessStatusCode();
        Assert.Equal(child.Session.SessionId.ToString(), (await second.Content.ReadFromJsonAsync<ContinueInChatResponse>())!.SessionId);
        var stored = (await memory.LoadAsync(child.Session.SessionId))!;
        Assert.Equal(child.Session.Origin, stored.Origin);
        Assert.True(stored.Surfaces.HasFlag(SessionSurface.ChatList));
        Assert.True(stored.Surfaces.HasFlag(SessionSurface.BackgroundWork));
        Assert.Single(await runs.ListForSessionAsync(source.Owner, child.Session.SessionId));
        Assert.Equal(AgentRunStatus.Queued, (await runs.GetAsync(source.Owner, child.Run.AgentRunId))!.Status);
        var history = await client.GetFromJsonAsync<AgentRunPageResponse>(path + "/agent-runs?limit=1");
        Assert.Equal(child.Run.AgentRunId.ToString(), Assert.Single(history!.Items).AgentRunId);
        Assert.False(history.HasMore);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{parent.SessionId}/agent-runs/{child.Run.AgentRunId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + $"/agent-runs?before={source.AgentRunId}")).StatusCode);
        var stale = await client.PostAsJsonAsync(path + $"/agent-runs/{child.Run.AgentRunId}/cancel", new CancelAgentRunRequest(99));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var cancelled = await client.PostAsJsonAsync(path + $"/agent-runs/{child.Run.AgentRunId}/cancel", new CancelAgentRunRequest(child.Run.Revision));
        cancelled.EnsureSuccessStatusCode();
        Assert.Equal("cancelled", (await cancelled.Content.ReadFromJsonAsync<AgentRunResponse>())!.Status);
    }

    private sealed class PausedDispatchHost : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                foreach (var registration in services.Where(service => service.ServiceType == typeof(IHostedService)
                    && (service.ImplementationType == typeof(AgentRunHostedService) || service.ImplementationType == typeof(BackgroundOccurrenceIntakeHostedService))).ToArray())
                    services.Remove(registration);
            });
        }
    }
}
