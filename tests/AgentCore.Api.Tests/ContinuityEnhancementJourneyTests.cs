using AgentCore.Application.Execution;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Continuity;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ContinuityEnhancementJourneyTests
{

    [Fact(Timeout = 90000)]
    public async Task Active_maintenance_is_noop_until_observable_checkpoint_dedupes_and_recovers_pending_generation()
    {
        var db = Path.Combine(Path.GetTempPath(), $"continuity-maintenance-{Guid.NewGuid():N}.db");
        Guid instanceId, sessionId;
        var clock = new MaintenanceClock(DateTimeOffset.UtcNow);
        await using (var host = new ExperienceHost(db, clock: clock))
        {
            var s = host.Services;
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21)).InstanceId;
            await s.GetRequiredService<IExperienceStore>().ConfigureAsync(instanceId, 0, true);
            var source = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            sessionId = source.SessionId;
            var maintenance = s.GetRequiredService<ExperienceService>();
            await Assert.ThrowsAsync<AgentCoreException>(() => maintenance.RequestSessionAsync(instanceId, sessionId).AsTask());
            Assert.Empty(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100));
            var history = s.GetRequiredService<IMemoryStore>();
            var entry = new ConversationEntry(Guid.NewGuid(), 1, null, ConversationRole.Assistant, "", Guid.NewGuid(), EntryStatus.Completed, SessionMode.Text, 0, 0, DateTimeOffset.UtcNow);
            source = source with { Revision = source.Revision + 1, Status = SessionStatus.Attached, Entries = [entry], LastEntrySequence = 1 };
            await history.SaveAsync(source, source.Revision - 1);
            clock.Advance();
            await Assert.ThrowsAsync<AgentCoreException>(() => maintenance.RequestSessionAsync(instanceId, sessionId).AsTask()); Assert.Empty(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100));
            source = source with { Revision = source.Revision + 1, Entries = [entry with { Text = "Completed store audit", ReceivedTextEndExclusive = 21 }] };
            await history.SaveAsync(source, source.Revision - 1);
            clock.Advance();
            await maintenance.RequestSessionAsync(instanceId, sessionId); await maintenance.RequestSessionAsync(instanceId, sessionId);
            Assert.Single(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100));
            Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(new(instanceId, LocalUserProfile.Id), 100));
        }
        await using var reopened = new ExperienceHost(db, clock: clock);
        var services = reopened.Services;
        Assert.Equal(1, await services.ExecuteRunsAsync(100));
        var first = Assert.Single(await services.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100)); Assert.NotNull(first.Content);
        var memory = services.GetRequiredService<IMemoryStore>(); var snapshot = (await memory.LoadAsync(sessionId))!;
        var second = snapshot.Entries.Single() with { EntryId = Guid.NewGuid(), Sequence = 2, ResponseId = Guid.NewGuid(), Text = "Completed second audit", ReceivedTextEndExclusive = 22 };
        snapshot = snapshot with { Revision = snapshot.Revision + 1, Status = SessionStatus.Attached, Entries = [.. snapshot.Entries, second], LastEntrySequence = 2 };
        await memory.SaveAsync(snapshot, snapshot.Revision - 1);
        clock.Advance();
        await services.GetRequiredService<ExperienceService>().RequestSessionAsync(instanceId, sessionId);
        Assert.Equal(2, (await services.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100)).Count);
        Assert.Equal(SessionStatus.Attached, (await memory.LoadMetadataAsync(sessionId))!.Status);
    }
    private sealed class MaintenanceClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now += TimeSpan.FromMinutes(5);
    }

}
