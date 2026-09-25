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
    protected override Task ForEachProfileAsync(Func<PersonaHistoryFixture, Task> exercise)
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var events = new InMemoryAdminEventStore(ids);
        var instances = new InMemoryAgentInstanceStore { EventStore = events };
        var service = new AdminAgentInstanceService(
            new AdminInstanceDefinitionVersionHistoryTests.VersionedDefinitionStore(SampleDefinition()),
            instances,
            events,
            ids,
            clock);
        return exercise(new PersonaHistoryFixture(instances, events, service, clock));
    }

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

public sealed class SqliteAdminInstancePersonaHistoryTests : AdminInstancePersonaHistoryTests
{
    protected override async Task ForEachProfileAsync(Func<PersonaHistoryFixture, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-persona-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var clock = TimeProvider.System;
            var ids = new SystemIdGenerator(clock);
            var events = new SqliteAdminEventStore(factory, ids);
            var instances = new SqliteAgentInstanceStore(factory, ids);
            var service = new AdminAgentInstanceService(
                new AdminInstanceDefinitionVersionHistoryTests.VersionedDefinitionStore(SampleDefinition()),
                instances,
                events,
                ids,
                clock);
            await exercise(new PersonaHistoryFixture(instances, events, service, clock));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options)
        : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}

public abstract class AdminInstancePersonaHistoryTests
{
    protected sealed record PersonaHistoryFixture(
        IAgentInstanceStore Instances,
        IAdminEventStore Events,
        AdminAgentInstanceService Service,
        TimeProvider Clock);

    protected abstract Task ForEachProfileAsync(Func<PersonaHistoryFixture, Task> exercise);

    [Fact]
    public async Task Update_persona_records_persona_changed_event()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000703");
            var instance = AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId);
            await fixture.Instances.InsertAsync(instance, CancellationToken.None);
            var updated = await fixture.Service.UpdatePersonaAsync(
                instanceId,
                instance.Persona with { Tone = "Warmer" },
                1,
                1,
                CancellationToken.None);
            Assert.Equal(2, updated.PersonaRevision);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("agent.instance", instanceId.ToString("D")),
                CancellationToken.None);
            Assert.Contains(history, item => item.Operation == AdminEventOperationKind.PersonaChanged);
            var personaEvent = history.Single(item => item.Operation == AdminEventOperationKind.PersonaChanged);
            using var summary = JsonDocument.Parse(personaEvent.SummaryJson);
            Assert.Equal(1, summary.RootElement.GetProperty("fromPersonaRevision").GetInt64());
            Assert.Equal(2, summary.RootElement.GetProperty("personaRevision").GetInt64());
        });
    }

    [Fact]
    public async Task Retry_with_same_operation_id_returns_same_persona_revision_without_duplicate_history()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000704");
            var instance = AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId);
            await fixture.Instances.InsertAsync(instance, CancellationToken.None);
            var persona = instance.Persona with { Tone = "Edited" };
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000705");
            var append = AdminEventFactory.PersonaChanged(operationId, now, "examiner", instanceId, 1, 1, 2, persona);
            var update = new AgentInstanceRevisionUpdate(instanceId, 1, Persona: persona, ExpectedPersonaRevision: 1);
            var first = await fixture.Instances.UpdatePersonaWithHistoryAsync(update, now, append, CancellationToken.None);
            var second = await fixture.Instances.UpdatePersonaWithHistoryAsync(update, now, append, CancellationToken.None);
            Assert.Equal(first.PersonaRevision, second.PersonaRevision);
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.Single(history, item => item.Operation == AdminEventOperationKind.PersonaChanged);
        });
    }

    [Fact]
    public async Task Retry_with_same_operation_id_but_different_persona_payload_is_rejected()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000708");
            var instance = AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId);
            await fixture.Instances.InsertAsync(instance, CancellationToken.None);
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000709");
            var firstPersona = instance.Persona with { Tone = "First" };
            var firstAppend = AdminEventFactory.PersonaChanged(operationId, now, "examiner", instanceId, 1, 1, 2, firstPersona);
            _ = await fixture.Instances.UpdatePersonaWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instanceId, 1, Persona: firstPersona, ExpectedPersonaRevision: 1),
                now,
                firstAppend,
                CancellationToken.None);
            var conflictingPersona = instance.Persona with { Tone = "Different" };
            var conflictingAppend = AdminEventFactory.PersonaChanged(operationId, now, "examiner", instanceId, 1, 1, 2, conflictingPersona);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Instances.UpdatePersonaWithHistoryAsync(
                    new AgentInstanceRevisionUpdate(instanceId, 1, Persona: conflictingPersona, ExpectedPersonaRevision: 1),
                    now,
                    conflictingAppend,
                    CancellationToken.None).AsTask());
            Assert.Equal("Conflict", error.Code);
            Assert.Contains("does not match the retried command", error.Message, StringComparison.Ordinal);
            var reloaded = await fixture.Instances.FindAsync(instanceId, CancellationToken.None);
            Assert.Equal("First", reloaded!.Persona.Tone);
        });
    }

    [Fact]
    public async Task Retry_with_same_operation_id_but_different_persona_revision_is_rejected()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000706");
            var instance = AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId);
            await fixture.Instances.InsertAsync(instance, CancellationToken.None);
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000707");
            var firstPersona = instance.Persona with { Tone = "First" };
            var firstAppend = AdminEventFactory.PersonaChanged(operationId, now, "examiner", instanceId, 1, 1, 2, firstPersona);
            _ = await fixture.Instances.UpdatePersonaWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instanceId, 1, Persona: firstPersona, ExpectedPersonaRevision: 1),
                now,
                firstAppend,
                CancellationToken.None);
            var conflictingAppend = AdminEventFactory.PersonaChanged(
                operationId,
                now,
                "examiner",
                instanceId,
                1,
                1,
                3,
                instance.Persona with { Tone = "Conflict" });
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Instances.UpdatePersonaWithHistoryAsync(
                    new AgentInstanceRevisionUpdate(
                        instanceId,
                        1,
                        Persona: instance.Persona with { Tone = "Conflict" },
                        ExpectedPersonaRevision: 1),
                    now,
                    conflictingAppend,
                    CancellationToken.None).AsTask());
            Assert.Equal("Conflict", error.Code);
            Assert.Contains("does not match the retried command", error.Message, StringComparison.Ordinal);
            var reloaded = await fixture.Instances.FindAsync(instanceId, CancellationToken.None);
            Assert.Equal(2, reloaded!.PersonaRevision);
        });
    }

    protected static AgentDefinition SampleDefinition() =>
        AdminInstanceDefinitionVersionHistoryTests.SampleExaminerDefinition(1);
}
