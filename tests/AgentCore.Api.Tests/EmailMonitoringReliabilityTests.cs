using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Credentials;
using AgentCore.Application.Email;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class EmailMonitoringReliabilityTests
{
    [Theory(Timeout = 60000)]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    public async Task Mixed_account_bucket_retains_only_authorized_evidence(bool sqlite, int revoked)
    {
        await using var f = new Fixture(sqlite);
        var owner = await f.Owner(); var automation = await f.Automation(owner, "Coalesced", true);
        var accounts = new[] { await f.Account(owner, "first"), await f.Account(owner, "second") };
        await f.Monitor.RunOnceAsync(); f.Clock.Advance(61); await f.Monitor.RunOnceAsync();
        var receipts = await f.EmailEvents(); Assert.Equal(2, receipts.Length);
        Assert.Equal(0, await f.Dispatcher.RunOnceAsync());
        var bucket = Assert.Single(await f.Events.DueBucketsAsync(f.Clock.GetUtcNow().AddSeconds(60)));
        Assert.Equal(2, bucket.Sources.Count);
        await f.Monitor.ConfigureAsync(accounts[revoked == 1 ? 1 : 0].CredentialId, 1, false, 60);
        if (revoked == 2) await f.Monitor.ConfigureAsync(accounts[1].CredentialId, 1, false, 60);
        var mixedEvidence = AutomationRules.Evidence(automation, new {
            bucketId = bucket.BucketId, count = bucket.Sources.Count,
            sources = bucket.Sources.Select(s => new { eventId = s.EventId, data = JsonSerializer.Deserialize<JsonElement>(s.DataJson) })
        }, automation.Triggers.Single());
        var stale = new TriggerOccurrence(Guid.NewGuid(), $"race:{Guid.NewGuid():N}", automation.AutomationId, automation.Owner,
            TriggerSourceKind.CoreEvent, null, bucket.DueAtUtc, f.Clock.GetUtcNow(), mixedEvidence, bucket.BucketId,
            bucket.Subscription.TriggerRevision, OccurrenceRoutingDisposition.Pending, null, 0, null, null, null,
            triggerId: bucket.Subscription.TriggerId);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Triggers.AdmitOccurrenceAsync(stale).AsTask());
        f.Clock.Advance(61);
        Assert.Equal(revoked == 2 ? 0 : 1, await f.Dispatcher.RunOnceAsync());
        var occurrences = await f.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20);
        if (revoked == 2) { Assert.Empty(occurrences); return; }
        var occurrence = Assert.Single(occurrences); Assert.Equal(automation.AutomationId, occurrence.AutomationId);
        using var evidence = JsonDocument.Parse(occurrence.EvidenceJson);
        var context = evidence.RootElement.GetProperty("triggerContext");
        Assert.Equal(1, context.GetProperty("count").GetInt32());
        var source = Assert.Single(context.GetProperty("sources").EnumerateArray());
        Assert.Equal(accounts[revoked == 1 ? 0 : 1].CredentialId, source.GetProperty("data").GetProperty("accountId").GetGuid());
        Assert.Equal(0, await f.Dispatcher.RunOnceAsync()); // Replay cannot duplicate the retained delivery.
        Assert.Empty(await f.Events.DueBucketsAsync(f.Clock.GetUtcNow()));
    }

    [Theory(Timeout = 60000)] [InlineData(false)] [InlineData(true)]
    public async Task Check_now_and_failed_recovery_preserve_delivery_fence_until_success(bool sqlite)
    {
        await using var f = new Fixture(sqlite);
        var owner = await f.Owner(); await f.Automation(owner, "Inbox"); var account = await f.Account(owner, "inbox");
        await f.Monitor.RunOnceAsync(); f.Clock.Advance(61); await f.Monitor.RunOnceAsync();
        var receipt = Assert.Single(await f.EmailEvents());
        f.Feed.Permanent = true; await f.Monitor.CheckAsync(account.CredentialId, 1); await f.Monitor.RunOnceAsync();
        Assert.True((await f.Watches.GetAsync(account.CredentialId)).Paused);
        var queued = await f.Monitor.CheckAsync(account.CredentialId, 1);
        Assert.True(queued.CheckPending); Assert.Equal("Paused", queued.Status);
        Assert.False(await f.Watches.CanDeliverAsync(receipt.Event.EventId));
        Assert.Equal(0, await f.Dispatcher.RunOnceAsync()); // Dispatcher runs before the requested recovery poll.
        await f.Monitor.RunOnceAsync();
        Assert.True((await f.Watches.GetAsync(account.CredentialId)).Paused);
        Assert.False(await f.Watches.CanDeliverAsync(receipt.Event.EventId));
        Assert.Empty(await f.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20));
        Assert.Equal(EventMatchStatus.PolicySkipped, Assert.Single(await f.Events.DeliveriesAsync(receipt.Event.EventId)).Status);
        // A transient failure during recovery must also preserve the prior permanent pause.
        f.Feed.Permanent = false; f.Feed.Temporary = true;
        await f.Monitor.CheckAsync(account.CredentialId, 1); await f.Monitor.RunOnceAsync();
        Assert.True((await f.Watches.GetAsync(account.CredentialId)).Paused);
        Assert.False(await f.Watches.CanDeliverAsync(receipt.Event.EventId));
        f.Feed.Temporary = false; f.Feed.Block = true;
        await f.Monitor.CheckAsync(account.CredentialId, 1);
        var running = f.Monitor.RunOnceAsync().AsTask(); await f.Feed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True((await f.Watches.GetAsync(account.CredentialId)).Paused);
            Assert.False(await f.Watches.CanDeliverAsync(receipt.Event.EventId)); // Claiming is not successful authorization.
        }
        finally { f.Feed.Release.TrySetResult(); await running; }
        Assert.False((await f.Watches.GetAsync(account.CredentialId)).Paused);
        Assert.True(await f.Watches.CanDeliverAsync(receipt.Event.EventId));
        Assert.Equal(0, await f.Dispatcher.RunOnceAsync()); // Terminally skipped receipts never revive.
    }

    [Theory(Timeout = 60000)] [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Email_commit_freezes_subscribers_and_removal_still_revokes_delivery(bool sqlite, bool removeOriginal)
    {
        await using var f = new Fixture(sqlite);
        var owner = await f.Owner(); var original = await f.Automation(owner, "Original"); await f.Account(owner, "inbox");
        await f.Monitor.RunOnceAsync(); f.Clock.Advance(61); await f.Monitor.RunOnceAsync();
        var receipt = Assert.Single(await f.EmailEvents()); Assert.True(receipt.Snapshotted);
        Assert.Equal(original.AutomationId, Assert.Single(await f.Events.DeliveriesAsync(receipt.Event.EventId)).Subscription.AutomationId);
        if (removeOriginal) await f.Host.Services.GetRequiredService<IAutomationService>().CancelAsync(original.Owner, original.AutomationId, original.Revision);
        var added = await f.Automation(owner, "Added later");
        Assert.Equal(removeOriginal ? 0 : 1, await f.Dispatcher.RunOnceAsync());
        var occurrences = await f.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20);
        Assert.DoesNotContain(occurrences, o => o.AutomationId == added.AutomationId);
        if (!removeOriginal) Assert.Equal(original.AutomationId, Assert.Single(occurrences).AutomationId);
        else Assert.Empty(occurrences);
        // Later detections use the updated subscription set.
        f.Feed.Message = "later"; f.Clock.Advance(61); await f.Monitor.RunOnceAsync();
        Assert.Equal(removeOriginal ? 1 : 2, await f.Dispatcher.RunOnceAsync());
        Assert.Contains(await f.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20), o => o.AutomationId == added.AutomationId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Clock Clock { get; } = new(); public Feed Feed { get; } = new();
        public WebApplicationFactory<Program> Host { get; }
        private readonly HttpClient client;
        public Fixture(bool sqlite)
        {
            void Configure(IServiceCollection services)
            {
                services.RemoveAll<IEmailChangeFeed>(); services.AddSingleton<IEmailChangeFeed>(Feed);
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                foreach (var h in services.Where(s => s.ImplementationType == typeof(EmailWatchHostedService) || s.ImplementationType == typeof(TriggerSchedulerHostedService)).ToArray()) services.Remove(h);
            }
            Host = sqlite ? new ExperienceHost(Path.Combine(Path.GetTempPath(), $"email-reliability-{Guid.NewGuid():N}.db"), clock: Clock, configure: Configure)
                : new AgentCoreApiFactory().WithWebHostBuilder(b => b.ConfigureTestServices(Configure));
            client = Host.CreateClient();
        }
        public EmailMonitoringService Monitor => Host.Services.GetRequiredService<EmailMonitoringService>();
        public IEmailWatchStore Watches => Host.Services.GetRequiredService<IEmailWatchStore>();
        public ICoreEventStore Events => Host.Services.GetRequiredService<ICoreEventStore>();
        public ITriggerStore Triggers => Host.Services.GetRequiredService<ITriggerStore>();
        public CoreEventDispatcher Dispatcher => Host.Services.GetRequiredService<CoreEventDispatcher>();
        public async Task<Guid> Owner() => (await Host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 25)).InstanceId;
        public async Task<CredentialView> Account(Guid owner, string reference)
        {
            var credentials = Host.Services.GetRequiredService<CredentialService>();
            var account = await credentials.CreateEmailAccountAsync(reference, EmailAccountProfile.Custom($"{reference}@example.test"), "fixture-password");
            var instance = (await Host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner))!;
            await credentials.BindAsync(owner, account.CredentialId, reference, instance.Revision);
            await Monitor.ConfigureAsync(account.CredentialId, 0, true, 60); return account;
        }
        public async Task<Automation> Automation(Guid owner, string name, bool coalesce = false)
        {
            await Host.Services.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(owner, null, 0, true, name, "Inspect inbox evidence",
                new CoreEventTrigger("email.message.received", null,
                    coalesce ? new(EventDispatchMode.CoalesceLatest, 60) : new(EventDispatchMode.EveryMatch)), null, null,
                executionTarget: AutomationExecutionTarget.Background, completionDelivery: AutomationCompletionDelivery.None);
            return Assert.Single(await Triggers.ListAsync(new(owner, LocalUserProfile.Id), AutomationStatus.Active), a => a.Name == name);
        }
        public async Task<CoreEventReceipt[]> EmailEvents() => (await Events.PendingAsync()).Where(e => e.Event.Key == "email.message.received").ToArray();
        public async ValueTask DisposeAsync() { client.Dispose(); await Host.DisposeAsync(); }
    }
    private sealed class Clock : TimeProvider
    { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int seconds) => now = now.AddSeconds(seconds); }
    private sealed class Feed : IEmailChangeFeed
    {
        public bool IsSynthetic => true;
        public bool Permanent, Temporary, Block; public string Message = "fresh";
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<EmailWatchCursor> BaselineAsync(Credential account, CancellationToken ct) => ValueTask.FromResult(new EmailWatchCursor(EmailWatchCursorKind.Synthetic));
        public async ValueTask<EmailWatchBatch> PollAsync(Credential account, EmailWatchCursor cursor, CancellationToken ct)
        {
            if (Permanent) throw new MailAuthorizationUnavailableException();
            if (Temporary) throw new IOException("Fixture failure");
            if (Block) { Started.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return new([$"syn-{account.CredentialId:N}:{Message}"], cursor with { Offset = cursor.Offset + 1 });
        }
    }
}
