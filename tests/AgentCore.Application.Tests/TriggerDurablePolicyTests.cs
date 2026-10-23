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

    [Theory]
    [InlineData(true, "", false, false, false)]
    [InlineData(true, "schedule", true, false, false)]
    [InlineData(true, "coreEvent", false, true, false)]
    [InlineData(true, "applicationEvent", false, false, true)]
    [InlineData(true, "schedule,coreEvent,applicationEvent", true, true, true)]
    [InlineData(false, "schedule,coreEvent,applicationEvent", false, false, false)]
    public void Configured_source_permissions_remain_independent_and_require_enabled_policy(
        bool enabled, string kinds, bool schedule, bool builtin, bool webhook)
    {
        var definition = SampleDefinitions.Examiner with
        { TriggerPolicy = new(enabled, false, false, false, false, false, 1, 1, 1,
            kinds.Split(',', StringSplitOptions.RemoveEmptyEntries)) };
        Assert.Equal(schedule, OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule));
        Assert.Equal(builtin, OccurrenceCompatibility.Allows(definition, TriggerSourceKind.CoreEvent));
        Assert.Equal(webhook, OccurrenceCompatibility.Allows(definition, TriggerSourceKind.ApplicationEvent));
        Assert.True(OccurrenceCompatibility.Allows(definition, TriggerSourceKind.ManualInvocation));
    }

    [Fact]
    public async Task Policy_recovery_reactivates_suspended_registration_when_eligible()
    {
        var definitions = Definitions();
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var v9 = (await definitions.GetAsync("general-assistant", 9))!;
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000099");
        await instances.InsertAsync(new AgentInstance(
            instanceId,
            v9.Id,
            9,
            v9.Identity,
            AgentInstanceLifecycle.Active,
            Now,
            Now));
        await memory.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now)
            }, Now),
            0);

        var store = new InMemoryTriggerStore();
        var owner = new TriggerOwner(instanceId, ProfileId);
        var due = Now.AddMinutes(5);
        var registration = new Automation(
            Guid.Parse("019944af-00d2-7000-8000-000000000002"),
            owner,
            AutomationStatus.SuspendedPolicy,
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
        var reactivated = (await store.GetAsync(owner, registration.AutomationId))!;
        Assert.Equal(AutomationStatus.Active, reactivated.Status);
        Assert.Null(reactivated.SuspensionReason);
        Assert.Equal(due, reactivated.NextOccurrenceAtUtc);
    }

    private static IAgentDefinitionStore Definitions()
    {
        var builtIns = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var current = builtIns.GetAsync("general-assistant", 21).GetAwaiter().GetResult()!;
        return new PolicyDefinitions(current);
    }

    private sealed class PolicyDefinitions(AgentDefinition current) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([current with { Version = 7, TriggerPolicy = null }, current with { Version = 9 }]);
        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(id != current.Id ? null : version == 7 ? current with { Version = 7, TriggerPolicy = null } : current with { Version = 9 });
    }

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
        public ValueTask<bool> HasBackingInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.HasBackingInstanceAsync(instanceId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentInstance>> ListAsync(int limit, CancellationToken cancellationToken = default) =>
            inner.ListAsync(limit, cancellationToken);

        public ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(instanceId, cancellationToken);

        public async ValueTask InsertAsync(AgentInstance instance, CancellationToken cancellationToken = default, IReadOnlyList<SkillSpec>? initialSkills = null)
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
            CancellationToken cancellationToken = default, IReadOnlyList<AgentCore.Domain.Definitions.SkillSpec>? initialSkills = null) =>
            inner.InsertManagedWithHistoryAsync(instance, historyAppend, cancellationToken);

        public ValueTask<AgentInstance> UpdateActiveVersionWithHistoryAsync(
            AgentInstanceRevisionUpdate update,
            DateTimeOffset updatedAt,
            AdminEventAppend historyAppend,
            CancellationToken cancellationToken = default) =>
            inner.UpdateActiveVersionWithHistoryAsync(update, updatedAt, historyAppend, cancellationToken);

        public ValueTask<AgentInstance> UpdatePersonaWithHistoryAsync(
            AgentInstanceRevisionUpdate update,
            DateTimeOffset updatedAt,
            AdminEventAppend historyAppend,
            CancellationToken cancellationToken = default) =>
            inner.UpdatePersonaWithHistoryAsync(update, updatedAt, historyAppend, cancellationToken);

        public ValueTask<AgentInstance> UpdateLifecycleWithHistoryAsync(
            AgentInstanceRevisionUpdate update,
            DateTimeOffset updatedAt,
            AdminEventAppend historyAppend,
            CancellationToken cancellationToken = default) =>
            inner.UpdateLifecycleWithHistoryAsync(update, updatedAt, historyAppend, cancellationToken);
    }
}
