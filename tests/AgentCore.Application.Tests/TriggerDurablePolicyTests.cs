using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Identity;
using AgentCore.Application.Testing;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class TriggerDurablePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = Guid.Parse("019944af-00d1-7000-8000-0000000000b1");

    [Fact]
    public async Task Schedule_create_uses_durable_active_version_not_session_definition()
    {
        var definitions = Definitions();
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var v7 = (await definitions.GetAsync("general-assistant", 7))!;
        var v9 = (await definitions.GetAsync("general-assistant", 9))!;
        var instanceId = AgentInstance.CompatibilityFor("general-assistant");
        await instances.InsertAsync(new AgentInstance(
            instanceId,
            v7.Id,
            7,
            v7.Identity,
            AgentInstanceLifecycle.Active,
            Now,
            Now,
            Compatibility: true));
        await memory.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now)
            }, Now),
            0);

        var store = new InMemoryTriggerStore();
        var time = new FakeTimeProvider(Now);
        var registrations = new TriggerRegistrationService(store, Ids(4), time);
        var owner = new TriggerOwner(instanceId, ProfileId);
        var context = new TriggerCommandContext(
            owner,
            Guid.Parse("019944af-00d1-7000-8000-0000000000c1"),
            "UTC",
            "remind me tomorrow at 9",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Create,
            false,
            null,
            Guid.Parse("019944af-00d1-7000-8000-0000000000d1"),
            Now);

        using var args = JsonDocument.Parse("""{"intent":"Hello","relativeDayOffset":1,"localTime":"09:00"}""");
        var denied = await TriggerScheduleCommands.ExecuteAsync(
            v9,
            registrations,
            ToolCatalog.TriggerScheduleOnce,
            args.RootElement,
            context,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer(),
            instances,
            definitions,
            memory);
        Assert.Contains("\"error\":\"policy\"", denied.Text, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(owner, null));
    }

    [Fact]
    public async Task Compatibility_instance_forward_upgrades_and_due_schedule_is_admitted()
    {
        var definitions = Definitions();
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var clock = new FakeTimeProvider(Now);
        var instanceService = new AgentInstanceService(instances, definitions, memory, Ids(8), clock);
        var v9 = (await definitions.GetAsync("general-assistant", 9))!;
        await instances.InsertAsync(new AgentInstance(
            AgentInstance.CompatibilityFor("general-assistant"),
            "general-assistant",
            7,
            v9.Identity,
            AgentInstanceLifecycle.Active,
            Now,
            Now,
            Compatibility: true));
        await memory.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now)
            }, Now),
            0);

        var resolved = await instanceService.ResolveCompatibilityAsync(v9);
        Assert.Equal(9, resolved.ActiveVersion);

        var store = new InMemoryTriggerStore();
        var registrations = new TriggerRegistrationService(store, Ids(4, "019944af-00d2-7000-8000-"), clock);
        var owner = new TriggerOwner(resolved.InstanceId, ProfileId);
        var due = Now.AddMinutes(1);
        var tools = new SessionToolExecutor(
            triggerRegistrations: registrations,
            agentInstances: instances,
            agentDefinitions: definitions);
        var snapshot = new SessionSnapshot(
            1,
            Guid.Parse("019944af-00d2-7000-8000-000000000001"),
            1,
            v9,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            ProfileId,
            Now,
            Now,
            AgentInstanceId: resolved.InstanceId);
        await memory.SaveAsync(snapshot, 0);
        var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            memory,
            new CapturingSessionOutput(),
            Ids(16, "019944af-00d3-7000-8000-"),
            clock,
            NullLogger<SessionRuntime>.Instance,
            tools: tools);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("say hello to me in 1 minute"));
        await runtime.WaitUntilIdleAsync();
        var created = Assert.Single(await store.ListAsync(owner, null));
        Assert.Equal(TriggerRegistrationStatus.Active, created.Status);

        clock.Advance(TimeSpan.FromMinutes(1));
        var guard = new TriggerAdmissionGuard(instances, definitions, memory);
        var scheduler = new TriggerScheduler(store, NullLogger<TriggerScheduler>.Instance, guard);
        var pass = await scheduler.RunOnceAsync(clock.GetUtcNow());
        Assert.Equal(1, pass.Admitted);
        Assert.Equal(TriggerRegistrationStatus.Completed, (await store.GetAsync(owner, created.RegistrationId))!.Status);
    }

    [Fact]
    public async Task Archived_managed_instance_denies_schedule_create_and_due_admission()
    {
        var definitions = Definitions();
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var clock = new FakeTimeProvider(Now);
        var instanceService = new AgentInstanceService(instances, definitions, memory, Ids(8), clock);
        var v9 = (await definitions.GetAsync("general-assistant", 9))!;
        var managed = await instanceService.CreateAsync("general-assistant", 9);
        await memory.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now)
            }, Now),
            0);

        var store = new InMemoryTriggerStore();
        var registrations = new TriggerRegistrationService(store, Ids(4, "019944af-00d2-7000-8000-"), clock);
        var owner = new TriggerOwner(managed.InstanceId, ProfileId);
        var due = Now.AddMinutes(1);
        await store.CreateAsync(new TriggerRegistration(
            Guid.Parse("019944af-00d2-7000-8000-000000000001"),
            owner,
            TriggerRegistrationStatus.Active,
            "Hello",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null));

        var archived = await instanceService.SetLifecycleAsync(
            managed.InstanceId,
            AgentInstanceLifecycle.Archived,
            managed.Revision);
        Assert.Equal(AgentInstanceLifecycle.Archived, archived.Lifecycle);

        var context = new TriggerCommandContext(
            owner,
            Guid.NewGuid(),
            "UTC",
            "remind me tomorrow at 9",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Create,
            false,
            null,
            Guid.NewGuid(),
            Now);
        using var args = JsonDocument.Parse("""{"intent":"Hello","relativeDayOffset":1,"localTime":"09:00"}""");
        var denied = await TriggerScheduleCommands.ExecuteAsync(
            v9,
            registrations,
            ToolCatalog.TriggerScheduleOnce,
            args.RootElement,
            context,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer(),
            instances,
            definitions,
            memory);
        Assert.Contains("\"error\":\"policy\"", denied.Text, StringComparison.Ordinal);

        var guard = new TriggerAdmissionGuard(instances, definitions, memory);
        var applicationEvent = await guard.EvaluateAsync(owner, TriggerSourceKind.ApplicationEvent, CancellationToken.None);
        Assert.Equal(TriggerAdmissionDecisionKind.Suspend, applicationEvent.Kind);
        Assert.Contains("not active", applicationEvent.Reason, StringComparison.OrdinalIgnoreCase);

        clock.Advance(TimeSpan.FromMinutes(1));
        var scheduler = new TriggerScheduler(store, NullLogger<TriggerScheduler>.Instance, guard);
        var pass = await scheduler.RunOnceAsync(clock.GetUtcNow());
        Assert.Equal(0, pass.Admitted);
        var afterDue = (await store.ListAsync(owner, null))[0];
        Assert.Equal(TriggerRegistrationStatus.SuspendedPolicy, afterDue.Status);
        Assert.Contains("not active", afterDue.SuspensionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schedule_create_denies_when_durable_instance_is_missing()
    {
        var definitions = Definitions();
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var v9 = (await definitions.GetAsync("general-assistant", 9))!;
        var instanceId = AgentInstance.CompatibilityFor("general-assistant");
        await memory.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now)
            }, Now),
            0);

        var store = new InMemoryTriggerStore();
        var registrations = new TriggerRegistrationService(store, Ids(4), new FakeTimeProvider(Now));
        var owner = new TriggerOwner(instanceId, ProfileId);
        var context = new TriggerCommandContext(
            owner,
            Guid.NewGuid(),
            "UTC",
            "remind me tomorrow at 9",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Create,
            false,
            null,
            Guid.NewGuid(),
            Now);

        using var args = JsonDocument.Parse("""{"intent":"Hello","relativeDayOffset":1,"localTime":"09:00"}""");
        var denied = await TriggerScheduleCommands.ExecuteAsync(
            v9,
            registrations,
            ToolCatalog.TriggerScheduleOnce,
            args.RootElement,
            context,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer(),
            instances,
            definitions,
            memory);
        Assert.Contains("\"error\":\"policy\"", denied.Text, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(owner, null));
    }

    [Fact]
    public async Task Compatibility_insert_conflict_still_forward_aligns_requested_version()
    {
        var definitions = Definitions();
        var memory = new InMemoryMemoryStore();
        var clock = new FakeTimeProvider(Now);
        var v9 = (await definitions.GetAsync("general-assistant", 9))!;
        var v7 = (await definitions.GetAsync("general-assistant", 7))!;
        var inner = new InMemoryAgentInstanceStore();
        var conflictOnInsert = new ConflictOnInsertInstanceStore(
            inner,
            new AgentInstance(
                AgentInstance.CompatibilityFor("general-assistant"),
                v7.Id,
                7,
                v7.Identity,
                AgentInstanceLifecycle.Active,
                Now,
                Now,
                Compatibility: true));
        var service = new AgentInstanceService(conflictOnInsert, definitions, memory, Ids(8), clock);
        var resolved = await service.ResolveCompatibilityAsync(v9);
        Assert.Equal(9, resolved.ActiveVersion);
        Assert.Equal(9, (await inner.FindCompatibilityAsync("general-assistant"))!.ActiveVersion);
    }

    [Fact]
    public async Task Policy_recovery_reactivates_suspended_registration_when_eligible()
    {
        var definitions = Definitions();
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var v9 = (await definitions.GetAsync("general-assistant", 9))!;
        var instanceId = AgentInstance.CompatibilityFor("general-assistant");
        await instances.InsertAsync(new AgentInstance(
            instanceId,
            v9.Id,
            9,
            v9.Identity,
            AgentInstanceLifecycle.Active,
            Now,
            Now,
            Compatibility: true));
        await memory.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now)
            }, Now),
            0);

        var store = new InMemoryTriggerStore();
        var owner = new TriggerOwner(instanceId, ProfileId);
        var due = Now.AddMinutes(5);
        var registration = new TriggerRegistration(
            Guid.Parse("019944af-00d2-7000-8000-000000000002"),
            owner,
            TriggerRegistrationStatus.SuspendedPolicy,
            "Hello",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            "Scheduling is disabled for this agent.");
        await store.CreateAsync(registration);

        var guard = new TriggerAdmissionGuard(instances, definitions, memory);
        var recovery = new TriggerPolicyRecoveryService(store, guard);
        Assert.Equal(1, await recovery.ReactivateSuspendedForOwnerAsync(owner, Now));
        var reactivated = (await store.GetAsync(owner, registration.RegistrationId))!;
        Assert.Equal(TriggerRegistrationStatus.Active, reactivated.Status);
        Assert.Null(reactivated.SuspensionReason);
        Assert.Equal(due, reactivated.NextOccurrenceAtUtc);
    }

    private static FileAgentDefinitionStore Definitions() =>
        new(FindAgents(), SyntheticProviderAliases.Default);

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private static DeterministicIdGenerator Ids(int count, string prefix = "019944af-00d1-7000-8000-") =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf15")]);

    private sealed class ConflictOnInsertInstanceStore(IAgentInstanceStore inner, AgentInstance racedWinner) : IAgentInstanceStore
    {
        public ValueTask<IReadOnlyList<AgentInstance>> ListAsync(int limit, CancellationToken cancellationToken = default) =>
            inner.ListAsync(limit, cancellationToken);

        public ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(instanceId, cancellationToken);

        public ValueTask<AgentInstance?> FindCompatibilityAsync(string definitionId, CancellationToken cancellationToken = default) =>
            inner.FindCompatibilityAsync(definitionId, cancellationToken);

        public async ValueTask InsertAsync(AgentInstance instance, CancellationToken cancellationToken = default)
        {
            await inner.InsertAsync(racedWinner, cancellationToken).ConfigureAwait(false);
            throw new AgentCoreException("Conflict", "Agent instance already exists.", 409);
        }

        public ValueTask UpdateActiveVersionAsync(
            Guid instanceId,
            int activeVersion,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            inner.UpdateActiveVersionAsync(instanceId, activeVersion, updatedAt, cancellationToken);

        public ValueTask<AgentInstance> UpdateWithExpectedRevisionAsync(
            AgentInstanceRevisionUpdate update,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            inner.UpdateWithExpectedRevisionAsync(update, updatedAt, cancellationToken);

        public ValueTask<AgentInstance> InsertManagedWithHistoryAsync(
            AgentInstance instance,
            AdminEventAppend historyAppend,
            CancellationToken cancellationToken = default) =>
            inner.InsertManagedWithHistoryAsync(instance, historyAppend, cancellationToken);
    }
}
