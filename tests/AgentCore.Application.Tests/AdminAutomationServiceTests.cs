using AgentCore.Application.Admin;
using AgentCore.Application.Identity;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AdminAutomationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 16, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = LocalUserProfile.Id;
    private static readonly Guid SourceSessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");

    [Fact]
    public async Task List_maps_safe_provenance_fields()
    {
        var clock = new FakeTimeProvider(Now);
        var instances = new InMemoryAgentInstanceStore();
        var definitions = new VersionedDefinitions(SampleDefinitions.Examiner with { TriggerPolicy = SchedulingPolicy() });
        var sessions = new InMemoryMemoryStore();
        var instanceService = new AgentInstanceService(instances, definitions, sessions, Ids(4), clock);
        var managed = await instanceService.CreateAsync("examiner", 1);
        await sessions.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>(StringComparer.Ordinal), Now),
            0);

        var store = new InMemoryTriggerStore();
        var triggers = new TriggerRegistrationService(store, Ids(4, "019944af-00f4-7000-8000-"), clock);
        var owner = new TriggerOwner(managed.InstanceId, ProfileId);
        var due = Now.AddHours(2);
        await store.CreateAsync(new TriggerRegistration(
            Guid.Parse("019944af-00f4-7000-8000-000000000001"),
            owner,
            TriggerRegistrationStatus.Active,
            "Check in",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, SourceSessionId, null, Now, Now),
            null));

        var admin = new AdminAutomationService(instances, triggers, new FixedLocalProfile(ProfileId, clock));
        var rows = await admin.ListRegistrationsAsync(managed.InstanceId);
        var row = Assert.Single(rows);
        Assert.Equal(TriggerAuthorizationOrigin.CurrentUserTurn.ToString(), row.Provenance.AuthorizationOrigin);
        Assert.Equal(SourceSessionId.ToString("D"), row.Provenance.SourceSessionId);
        Assert.Equal(Now, row.Provenance.CreatedAt);
        Assert.Equal(Now, row.Provenance.UpdatedAt);
    }

    [Fact]
    public async Task Cancel_active_registration_succeeds()
    {
        var (admin, instanceId, registrationId, revision) = await CreateListedRegistrationAsync(
            TriggerRegistrationStatus.Active,
            suspensionReason: null);
        var cancelled = await admin.CancelRegistrationAsync(instanceId, registrationId, revision);
        Assert.Equal(TriggerRegistrationStatus.Cancelled, cancelled.Status);
        Assert.Equal(revision + 1, cancelled.Revision);
    }

    [Fact]
    public async Task Cancel_suspended_policy_registration_succeeds()
    {
        var (admin, instanceId, registrationId, revision) = await CreateListedRegistrationAsync(
            TriggerRegistrationStatus.SuspendedPolicy,
            suspensionReason: "Trigger policy ineligible");
        var cancelled = await admin.CancelRegistrationAsync(instanceId, registrationId, revision);
        Assert.Equal(TriggerRegistrationStatus.Cancelled, cancelled.Status);
        Assert.Equal(revision + 1, cancelled.Revision);
    }

    [Fact]
    public async Task Cancel_stale_revision_returns_conflict()
    {
        var (admin, instanceId, registrationId, revision) = await CreateListedRegistrationAsync(
            TriggerRegistrationStatus.Active,
            suspensionReason: null);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            admin.CancelRegistrationAsync(instanceId, registrationId, revision - 1).AsTask());
        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task Cancel_cross_instance_registration_returns_not_found()
    {
        var clock = new FakeTimeProvider(Now);
        var instances = new InMemoryAgentInstanceStore();
        var definitions = new VersionedDefinitions(SampleDefinitions.Examiner with { TriggerPolicy = SchedulingPolicy() });
        var sessions = new InMemoryMemoryStore();
        var instanceService = new AgentInstanceService(instances, definitions, sessions, Ids(8), clock);
        var owned = await instanceService.CreateAsync("examiner", 1);
        var other = await instanceService.CreateAsync("examiner", 1);
        await sessions.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>(StringComparer.Ordinal), Now),
            0);

        var registrationId = Guid.Parse("019944af-00f4-7000-8000-000000000003");
        var store = new InMemoryTriggerStore();
        var triggers = new TriggerRegistrationService(store, Ids(4, "019944af-00f4-7000-8000-"), clock);
        var owner = new TriggerOwner(owned.InstanceId, ProfileId);
        var due = Now.AddHours(2);
        await store.CreateAsync(new TriggerRegistration(
            registrationId,
            owner,
            TriggerRegistrationStatus.Active,
            "Owned",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, SourceSessionId, null, Now, Now),
            null));

        var admin = new AdminAutomationService(instances, triggers, new FixedLocalProfile(ProfileId, clock));
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            admin.CancelRegistrationAsync(other.InstanceId, registrationId, 1).AsTask());
        Assert.Equal(404, error.StatusCode);
    }

    private static async Task<(AdminAutomationService Admin, Guid InstanceId, Guid RegistrationId, long Revision)>
        CreateListedRegistrationAsync(TriggerRegistrationStatus status, string? suspensionReason)
    {
        var clock = new FakeTimeProvider(Now);
        var instances = new InMemoryAgentInstanceStore();
        var definitions = new VersionedDefinitions(SampleDefinitions.Examiner with { TriggerPolicy = SchedulingPolicy() });
        var sessions = new InMemoryMemoryStore();
        var instanceService = new AgentInstanceService(instances, definitions, sessions, Ids(4), clock);
        var managed = await instanceService.CreateAsync("examiner", 1);
        await sessions.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>(StringComparer.Ordinal), Now),
            0);

        var registrationId = Guid.Parse("019944af-00f4-7000-8000-000000000002");
        var store = new InMemoryTriggerStore();
        var triggers = new TriggerRegistrationService(store, Ids(4, "019944af-00f4-7000-8000-"), clock);
        var owner = new TriggerOwner(managed.InstanceId, ProfileId);
        var due = Now.AddHours(2);
        await store.CreateAsync(new TriggerRegistration(
            registrationId,
            owner,
            status,
            "Check in",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, SourceSessionId, null, Now, Now),
            suspensionReason));

        var admin = new AdminAutomationService(instances, triggers, new FixedLocalProfile(ProfileId, clock));
        return (admin, managed.InstanceId, registrationId, 1);
    }

    private static TriggerPolicy SchedulingPolicy() =>
        new(true, true, true, true, true, true, 32, 365, 1, [OccurrenceCompatibility.Schedule]);

    private static DeterministicIdGenerator Ids(int count, string prefix = "019944af-00f4-7000-8000-") =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            [Guid.Parse("019944af-00f4-7000-8000-0000000000ff")]);

    private sealed class VersionedDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) =>
            string.Equals(id, definition.Id, StringComparison.Ordinal)
                ? ValueTask.FromResult<AgentDefinition?>(definition)
                : ValueTask.FromResult<AgentDefinition?>(null);
    }

    private sealed class FixedLocalProfile(Guid profileId, TimeProvider clock) : ILocalUserProfileService
    {
        public ValueTask<UserProfile> GetLocalProfileAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new UserProfile(profileId, 1, new Dictionary<string, UserProfileValue>(StringComparer.Ordinal), clock.GetUtcNow()));

        public ValueTask<UserProfile> UpdateLocalProfileAsync(
            long expectedRevision,
            IReadOnlyDictionary<string, string?> values,
            UserProfileValueSource source,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
