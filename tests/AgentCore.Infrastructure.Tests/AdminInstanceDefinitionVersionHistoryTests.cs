using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class InMemoryAdminInstanceDefinitionVersionHistoryTests : AdminInstanceDefinitionVersionHistoryTests
{

    [Fact]
    public async Task Reassociate_rolls_back_when_history_append_fails()
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var instances = new InMemoryAgentInstanceStore { EventStore = new ThrowingAdminEventStore(ids) };
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000601");
        var instance = AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId);
        await instances.InsertAsync(instance, CancellationToken.None);
        var append = AdminEventFactory.InstanceDefinitionVersionChanged(
            Guid.Parse("019944af-00d1-7000-8000-000000000602"),
            now,
            "examiner",
            instanceId,
            1,
            2);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            instances.UpdateActiveVersionWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instanceId, 1, ActiveVersion: 2),
                now,
                append,
                CancellationToken.None).AsTask());
        var reloaded = await instances.FindAsync(instanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(1, reloaded!.ActiveVersion);
        Assert.Equal(1, reloaded.Revision);
    }
}

public abstract class AdminInstanceDefinitionVersionHistoryTests
{

    internal static AgentDefinition SampleExaminerDefinition(int version) =>
        new(
            2,
            "examiner",
            version,
            new AgentIdentity("Alex", "Examiner", "Practice.", "Calm"),
            ["Conduct practice"],
            "You are Alex.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-stt", "primary-tts"),
            new Dictionary<string, string>());

    internal sealed class VersionedDefinitionStore(params AgentDefinition[] definitions) : IAgentDefinitionStore
    {
        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                definitions.FirstOrDefault(item => item.Id == id && (version is null || item.Version == version)));

        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>(definitions);
    }
}
