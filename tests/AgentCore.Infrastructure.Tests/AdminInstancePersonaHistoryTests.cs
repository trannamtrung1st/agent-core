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

public sealed class InMemoryAdminInstancePersonaHistoryTests : AdminInstancePersonaHistoryTests
{

    [Fact]
    public async Task Update_rolls_back_when_history_append_fails()
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var instances = new InMemoryAgentInstanceStore { EventStore = new ThrowingAdminEventStore(ids) };
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000701");
        var instance = AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId);
        await instances.InsertAsync(instance, CancellationToken.None);
        var persona = instance.Persona with { Tone = "Updated" };
        var append = AdminEventFactory.PersonaChanged(
            Guid.Parse("019944af-00d1-7000-8000-000000000702"),
            now,
            "examiner",
            instanceId,
            1,
            1,
            2,
            persona);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            instances.UpdatePersonaWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instanceId, 1, Persona: persona, ExpectedPersonaRevision: 1),
                now,
                append,
                CancellationToken.None).AsTask());
        var reloaded = await instances.FindAsync(instanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(instance.Persona.Tone, reloaded!.Persona.Tone);
        Assert.Equal(1, reloaded.PersonaRevision);
    }
}

public abstract class AdminInstancePersonaHistoryTests
{

    protected static AgentDefinition SampleDefinition() =>
        AdminInstanceDefinitionVersionHistoryTests.SampleExaminerDefinition(1);
}
