using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using AgentCore.Infrastructure.Persistence;

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
        var artifacts = services.GetRequiredService<IArtifactStore>();
        for (var index = 0; index < 51; index++)
            await artifacts.CreateAsync(child.Session.SessionId, $"result-{index}.txt", "text/plain", "result"u8.ToArray(), null, null, agentRunId: child.Run.AgentRunId);
        var backgroundPath = $"/api/v2/agent-instances/{instanceId}/background-sessions";
        var listed = await client.GetFromJsonAsync<BackgroundSessionPageResponse>(backgroundPath);
        var row = Assert.Single(listed!.Items);
        Assert.Equal(child.Session.SessionId.ToString(), row.Session.SessionId);
        Assert.Equal(50, row.ArtifactCount);
        Assert.True(row.ArtifactCountHasMore);
        Assert.True(row.CanContinueInChat);
        Assert.DoesNotContain((await memory.ListCatalogAsync(null, 50, false)).Items, item => item.SessionId == child.Session.SessionId);
        var path = $"/api/v2/sessions/{child.Session.SessionId}";
        var instances = services.GetRequiredService<IAgentInstanceStore>();
        var instance = (await instances.FindAsync(instanceId))!;
        await instances.UpdateWithExpectedRevisionAsync(new AgentInstanceRevisionUpdate(instanceId, instance.Revision, Lifecycle: AgentInstanceLifecycle.Archived), now);
        var blocked = await client.GetFromJsonAsync<BackgroundSessionResponse>(path + "/background");
        Assert.False(blocked!.CanContinueInChat);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/continue-in-chat", new { })).StatusCode);
        Assert.False((await memory.LoadAsync(child.Session.SessionId))!.Surfaces.HasFlag(SessionSurface.ChatList));
        instance = (await instances.FindAsync(instanceId))!;
        await instances.UpdateWithExpectedRevisionAsync(new AgentInstanceRevisionUpdate(instanceId, instance.Revision, Lifecycle: AgentInstanceLifecycle.Active), now);
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

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_result_and_files_survive_followups_rename_and_sqlite_reopen(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), $"background-result-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "state.db");
        await using WebApplicationFactory<Program> factory = sqlite ? new StableSqliteHost(db, root) : new PausedDispatchHost();
        using var client = TestOwnerCapability.CreateOwnerClient(factory);
        var services = factory.Services;
        var instanceId = TestInstances.Create(client, "examiner", 1);
        var memory = services.GetRequiredService<IMemoryStore>();
        var runs = services.GetRequiredService<IAgentRunStore>();
        var parent = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
        var now = DateTimeOffset.UtcNow;
        var input = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User, "Delegate A", null, EntryStatus.Completed, SessionMode.Text, 0, 10, now);
        var proposed = parent with { Entries = [input], LastEntrySequence = 1, Revision = parent.Revision + 1 };
        var source = (await runs.AdmitAsync(proposed, parent.Revision, AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), proposed, [input], now, []))).Run;
        source = await runs.ApplyAsync(source.Owner, source.AgentRunId, new AgentRunCommand.Claim(source.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
        var child = BackgroundSessionAdmissionFactory.ForImmediate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), proposed, source, "stable-result", "Original objective", "Original title", true, now);
        await runs.AdmitImmediateAsync(child.Session, child.Run, source.Claim!.Generation);
        var original = await Complete(child.Run, "Result A", true);
        var artifacts = services.GetRequiredService<IArtifactStore>();
        var fileA = await artifacts.CreateAsync(child.Session.SessionId, "A.txt", "text/plain", "A"u8.ToArray(), null, null, agentRunId: original.AgentRunId);
        var path = $"/api/v2/sessions/{child.Session.SessionId}";
        var before = (await client.GetFromJsonAsync<BackgroundSessionResponse>(path + "/background"))!;
        foreach (var summary in new[] { "Result B", "Result C" })
        {
            var current = (await memory.LoadAsync(child.Session.SessionId))!;
            var turn = new ConversationEntry(Guid.NewGuid(), current.DurableLastEntrySequence + 1, Guid.NewGuid(), ConversationRole.User, summary, null, EntryStatus.Completed, SessionMode.Text, 0, summary.Length, now.AddSeconds(1));
            var updated = current with { Revision = current.Revision + 1, Entries = current.Entries.Append(turn).ToArray(), LastEntrySequence = turn.Sequence, Title = "Ongoing chat title" };
            var followup = (await runs.AdmitAsync(updated, current.Revision, AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), updated, [turn], now.AddSeconds(1), []))).Run;
            await Complete(followup, summary, false);
            await artifacts.CreateAsync(child.Session.SessionId, summary + ".txt", "text/plain", "later"u8.ToArray(), null, null, agentRunId: followup.AgentRunId);
        }
        await artifacts.CreateAsync(child.Session.SessionId, "legacy.txt", "text/plain", "unknown"u8.ToArray(), null, null);
        // Direct-store arrangement must finish before foregrounding creates a
        // mailbox-owned runtime which may persist its own detach checkpoint.
        (await client.PostAsJsonAsync(path + "/continue-in-chat", new { })).EnsureSuccessStatusCode();
        await Check(client);
        if (sqlite)
        {
            await factory.DisposeAsync();
            await using var reopened = new StableSqliteHost(db, root);
            using var reopenedClient = TestOwnerCapability.CreateOwnerClient(reopened);
            await Check(reopenedClient);
        }

        async Task<AgentRun> Complete(AgentRun run, string summary, bool attention)
        {
            run = await runs.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, now.AddSeconds(2), Guid.NewGuid(), now.AddMinutes(5)));
            var snapshot = (await memory.LoadAsync(run.SessionId))!;
            var answer = new ConversationEntry(Guid.NewGuid(), snapshot.DurableLastEntrySequence + 1, null, ConversationRole.Assistant, summary, run.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, summary.Length, now.AddSeconds(2));
            return await runs.CommitOutcomeAsync(snapshot with { Revision = snapshot.Revision + 1, Entries = snapshot.Entries.Append(answer).ToArray(), LastEntrySequence = answer.Sequence }, snapshot.Revision, run.Owner, run.AgentRunId,
                new AgentRunCommand.Complete(run.Revision, now.AddSeconds(2), run.Claim!.Generation, summary, attention ? AgentRunOutcomeKind.NeedsAttention : AgentRunOutcomeKind.Response, answer.EntryId), null);
        }
        async Task Check(HttpClient checkClient)
        {
            using var wire = JsonDocument.Parse(await checkClient.GetStringAsync(path + "/background"));
            Assert.Equal(original.AgentRunId.ToString(), wire.RootElement.GetProperty("initialRun").GetProperty("agentRunId").GetString());
            Assert.False(wire.RootElement.TryGetProperty("latestRun", out _));
            var after = (await checkClient.GetFromJsonAsync<BackgroundSessionResponse>(path + "/background"))!;
            Assert.Equal(before.InitialRun, after.InitialRun);
            Assert.Equal("Original title", after.OriginalTitle);
            Assert.Equal(before.Origin, after.Origin);
            Assert.Contains("ChatList", after.Surfaces);
            Assert.Equal(1, after.ArtifactCount); Assert.False(after.ArtifactCountHasMore);
            var list = (await checkClient.GetFromJsonAsync<BackgroundSessionPageResponse>($"/api/v2/agent-instances/{instanceId}/background-sessions"))!;
            Assert.Equal(after.InitialRun, Assert.Single(list.Items).InitialRun);
            var page = (await checkClient.GetFromJsonAsync<ArtifactPageResponse>(path + $"/artifacts/page?agentRunId={original.AgentRunId}"))!;
            Assert.Equal(fileA.ArtifactId.ToString(), Assert.Single(page.Items).ArtifactId);
            Assert.Equal(4, (await checkClient.GetFromJsonAsync<ArtifactPageResponse>(path + "/artifacts/page"))!.Items.Count);
            Assert.Equal(HttpStatusCode.NotFound, (await checkClient.GetAsync(path + $"/artifacts/page?agentRunId={source.AgentRunId}")).StatusCode);
            var history = (await checkClient.GetFromJsonAsync<AgentRunPageResponse>(path + "/agent-runs"))!;
            Assert.Equal(3, history.Items.Count);
            Assert.Equal(original.AgentRunId.ToString(), (await checkClient.GetFromJsonAsync<AgentRunResponse>(path + $"/agent-runs/{original.AgentRunId}"))!.AgentRunId);
            (await checkClient.PostAsJsonAsync(path + "/continue-in-chat", new { })).EnsureSuccessStatusCode();
            Assert.Equal(3, (await checkClient.GetFromJsonAsync<AgentRunPageResponse>(path + "/agent-runs"))!.Items.Count);
        }
    }

    private sealed class StableSqliteHost(string db, string root) : DurableSqliteHostFactory(db, runScheduler: false)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                services.RemoveAll<IArtifactStore>();
                services.AddSingleton<IArtifactStore>(provider => new SqliteArtifactStore(provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(), TimeProvider.System, Path.Combine(root, "artifacts")));
            });
        }
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
