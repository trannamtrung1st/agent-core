using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Credentials;
using AgentCore.Application.Email;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class EmailMonitoringRecoveryTests
{
    [Theory(Timeout = 60000)] [InlineData(false)] [InlineData(true)]
    public async Task Atomic_failure_replay_shared_polling_and_monitor_off_fences_match_in_both_stores(bool sqlite)
    {
        var clock = new Clock(); var feed = new Feed();
        var path = Path.Combine(Path.GetTempPath(), $"watch-recovery-{Guid.NewGuid():N}.db");
        void Configure(IServiceCollection services)
        {
            services.RemoveAll<IEmailChangeFeed>(); services.AddSingleton<IEmailChangeFeed>(feed);
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            foreach (var h in services.Where(s => s.ImplementationType == typeof(EmailWatchHostedService) || s.ImplementationType == typeof(TriggerSchedulerHostedService)).ToArray()) services.Remove(h);
        }
        await using WebApplicationFactory<Program> host = sqlite ? new ExperienceHost(path, clock: clock, configure: Configure)
            : new AgentCoreApiFactory().WithWebHostBuilder(b => b.ConfigureTestServices(Configure));
        var services = host.Services; using var client = TestOwnerCapability.CreateOwnerClient(host);
        var admin = services.GetRequiredService<AdminAgentInstanceService>(); var credentials = services.GetRequiredService<CredentialService>();
        var account = await credentials.CreateEmailAccountAsync("Shared watch", EmailAccountProfile.Custom("shared@example.test"), "fixture-password");
        var owners = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var owner = await admin.CreateManagedAsync("general-assistant", 25); owners.Add(owner.InstanceId);
            await credentials.BindAsync(owner.InstanceId, account.CredentialId, "inbox", owner.Revision);
            var r = await client.PostAsJsonAsync($"/api/v2/admin/agent-instances/{owner.InstanceId}/automations", new AutomationRequest(0, true, "Inbox", "Inspect inbox evidence", new AutomationTriggerDto("coreEvent", CoreEventKey: "email.message.received", Dispatch: new("everyMatch")), ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none")));
            r.EnsureSuccessStatusCode();
        }
        var restricted = await admin.CreateManagedAsync("general-assistant", 21);
        await credentials.BindAsync(restricted.InstanceId, account.CredentialId, "restricted-inbox", restricted.Revision);
        var denied = await client.PostAsJsonAsync($"/api/v2/admin/agent-instances/{restricted.InstanceId}/automations", new AutomationRequest(0, true, "Restricted inbox", "Inspect inbox evidence",
            new AutomationTriggerDto("coreEvent", CoreEventKey: "email.message.received", Dispatch: new("everyMatch")), ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none")));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode); // Catalog discovery cannot grant an older Definition Core Event authority.
        var monitoring = services.GetRequiredService<EmailMonitoringService>(); var watches = services.GetRequiredService<IEmailWatchStore>(); var events = services.GetRequiredService<ICoreEventStore>();
        await monitoring.ConfigureAsync(account.CredentialId, 0, true, 60); await monitoring.RunOnceAsync(); Assert.Equal(1, feed.Baselines); Assert.Equal(2, (await monitoring.GetAsync(account.CredentialId)).SubscribedInstances);
        clock.Advance(61); feed.Throw = true; await monitoring.RunOnceAsync();
        var failure = await watches.GetAsync(account.CredentialId); Assert.Equal(0, failure.Cursor!.Offset); Assert.Equal("email_watch_temporary_failure", failure.LastErrorCode); Assert.Empty(await events.PendingAsync());
        feed.Throw = false; await monitoring.CheckAsync(account.CredentialId, 1); await monitoring.RunOnceAsync();
        var pending = (await events.PendingAsync()).Where(r => r.Event.Key == "email.message.received").ToArray();
        Assert.Equal(2, pending.Length); Assert.Equal(2, pending.Select(e => e.Event.Owner.AgentInstanceId).Distinct().Count());
        Assert.Equal(2, feed.Polls); Assert.Equal(1, (await watches.GetAsync(account.CredentialId)).Cursor!.Offset);
        // Retry the identical upstream batch after commit (including duplicate refs).
        await monitoring.CheckAsync(account.CredentialId, 1); await monitoring.RunOnceAsync();
        Assert.Equal(2, (await events.PendingAsync()).Count(r => r.Event.Key == "email.message.received"));
        if (sqlite)
        {
            var reopened = new SqliteEmailWatchStore(services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCoreDbContext>>());
            Assert.Equal(1, (await reopened.GetAsync(account.CredentialId)).Cursor!.Offset); Assert.True(await reopened.CanDeliverAsync(pending[0].Event.EventId));
            var reopenedEvents = new SqliteCoreEventStore(services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCoreDbContext>>());
            Assert.Equal(2, (await reopenedEvents.PendingAsync()).Count(r => r.Event.Key == "email.message.received"));
        }
        feed.Permanent = true; await monitoring.CheckAsync(account.CredentialId, 1); await monitoring.RunOnceAsync();
        Assert.Equal("Paused", (await monitoring.GetAsync(account.CredentialId)).Status);
        Assert.False(await watches.CanDeliverAsync(pending[0].Event.EventId));
        var pollsAtPause = feed.Polls; clock.Advance(901); await monitoring.RunOnceAsync(); Assert.Equal(pollsAtPause, feed.Polls);
        feed.Permanent = false;
        feed.Block = true; await monitoring.CheckAsync(account.CredentialId, 1); var inFlight = monitoring.RunOnceAsync().AsTask();
        await feed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitoring.ConfigureAsync(account.CredentialId, 1, false, 60); feed.Release.SetResult(); await inFlight;
        Assert.Null((await watches.GetAsync(account.CredentialId)).Cursor); Assert.False(await watches.CanDeliverAsync(pending[0].Event.EventId));
        Assert.Equal(0, await services.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
        Assert.Empty(await services.GetRequiredService<ITriggerStore>().ListByDispositionAsync(AgentCore.Domain.Triggers.OccurrenceRoutingDisposition.Pending, 20));
        Assert.Empty(await services.GetRequiredService<IAgentRunStore>().ListAsync(new(owners[0], AgentCore.Domain.Conversation.LocalUserProfile.Id), 20));
    }
    private sealed class Clock : TimeProvider
    { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int s) => now = now.AddSeconds(s); }
    private sealed class Feed : IEmailChangeFeed
    {
        public bool IsSynthetic => true;
        public bool Throw, Block, Permanent; public int Baselines, Polls; public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<EmailWatchCursor> BaselineAsync(Credential c, CancellationToken ct) { Baselines++; return ValueTask.FromResult(new EmailWatchCursor(EmailWatchCursorKind.Synthetic, Offset: 0)); }
        public async ValueTask<EmailWatchBatch> PollAsync(Credential c, EmailWatchCursor cursor, CancellationToken ct)
        {
            Polls++; if (Permanent) throw new MailAuthorizationUnavailableException(); if (Throw) throw new IOException("Fixture transient transport failure");
            if (Block) { Started.SetResult(); await Release.Task.WaitAsync(ct); }
            return new([$"syn-{c.CredentialId:N}:new1", $"syn-{c.CredentialId:N}:new1"], cursor with { Offset = 1 });
        }
    }
}
