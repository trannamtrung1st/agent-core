using AgentCore.Application.Admin;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Identity;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AdminMemoryHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = LocalUserProfile.Id;

    [Fact]
    public async Task Delete_with_same_operation_id_is_idempotent()
    {
        var harness = await CreateHarnessAsync();
        var operationId = Guid.Parse("019944af-00e1-7000-8000-000000000001");
        var memoryId = harness.IdentityItem.MemoryId;

        await harness.History.DeleteWithHistoryAsync(
            harness.Instance.InstanceId,
            AdminLearnedMemoryScope.IdentityUser,
            memoryId,
            sessionId: null,
            operationId,
            Now);
        await harness.History.DeleteWithHistoryAsync(
            harness.Instance.InstanceId,
            AdminLearnedMemoryScope.IdentityUser,
            memoryId,
            sessionId: null,
            operationId,
            Now);

        var events = await harness.Events.ListAsync(new AdminEventListQuery(TargetType: "learned-memory.item", TargetId: memoryId.ToString("D")));
        Assert.Single(events);
    }

    [Fact]
    public async Task Reset_with_same_operation_id_returns_recorded_items_removed()
    {
        var harness = await CreateHarnessAsync();
        var operationId = Guid.Parse("019944af-00e2-7000-8000-000000000002");

        var first = await harness.History.ResetScopeWithHistoryAsync(
            harness.Instance.InstanceId,
            AdminLearnedMemoryScope.IdentityUser,
            sessionId: null,
            operationId,
            Now);
        var second = await harness.History.ResetScopeWithHistoryAsync(
            harness.Instance.InstanceId,
            AdminLearnedMemoryScope.IdentityUser,
            sessionId: null,
            operationId,
            Now);

        Assert.Equal(first.ItemsRemoved, second.ItemsRemoved);
        Assert.Equal(1, first.ItemsRemoved);
        var events = await harness.Events.ListAsync(new AdminEventListQuery(TargetType: "agent.instance", TargetId: harness.Instance.InstanceId.ToString("D")));
        Assert.Contains(events, item => item.Operation == AdminEventOperationKind.MemoryScopeReset);
    }

    [Fact]
    public async Task Retry_with_same_operation_id_but_different_memory_id_is_rejected()
    {
        var harness = await CreateHarnessAsync();
        var operationId = Guid.Parse("019944af-00e3-7000-8000-000000000003");
        await harness.History.DeleteWithHistoryAsync(
            harness.Instance.InstanceId,
            AdminLearnedMemoryScope.IdentityUser,
            harness.IdentityItem.MemoryId,
            sessionId: null,
            operationId,
            Now);

        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            harness.History.DeleteWithHistoryAsync(
                harness.Instance.InstanceId,
                AdminLearnedMemoryScope.IdentityUser,
                Guid.Parse("019944af-00e4-7000-8000-000000000004"),
                sessionId: null,
                operationId,
                Now).AsTask());
        Assert.Contains("does not match the retried command", error.Message, StringComparison.Ordinal);
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var clock = new FakeTimeProvider(Now);
        var ids = new SystemIdGenerator(clock);
        var events = new InMemoryAdminEventStore(ids);
        var sessions = new InMemoryMemoryStore();
        var structured = new InMemoryStructuredMemoryStore();
        var instances = new InMemoryAgentInstanceStore();
        var definition = SampleDefinitions.Examiner with
        {
            MemoryPolicy = new MemoryPolicy(
                SessionMemory: true,
                IdentityUserPromotion: true,
                IdentityUserRetrieval: true,
                UserPromotion: true,
                UserRetrieval: true)
        };
        var definitions = new VersionedDefinitions(definition);
        var instanceService = new AgentInstanceService(instances, definitions, sessions, Ids(8, "019944af-00a6-7000-8000-"), clock);
        var manager = new SessionManager(
            definitions,
            sessions,
            Ids(16, "019944af-00a7-7000-8000-"),
            clock,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            structuredMemory: structured,
            instances: instanceService);
        var memoryService = new StructuredMemoryService(structured, Ids(12, "019944af-00a8-7000-8000-"), clock);
        var adminMemory = new AdminMemoryService(
            instances,
            definitions,
            sessions,
            structured,
            memoryService,
            new FixedLocalProfile(ProfileId, clock));
        var durable = new InMemoryDurableState();
        var triggerRegistrations = new TriggerRegistrationService(new InMemoryTriggerStore(durable), ids, clock);
        var mutator = new InMemoryAdminP7eHistoryMutator(
            adminMemory,
            events,
            structured,
            new AdminAutomationService(instances, triggerRegistrations, new FixedLocalProfile(ProfileId, clock)),
            durable);
        var history = new AdminMemoryHistoryService(mutator, ids, clock);
        var instance = await instanceService.CreateAsync("examiner", 1);
        var session = await manager.CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var admission = new MemoryAdmissionContext("test", [], new HashSet<string>(StringComparer.Ordinal));
        var identity = await memoryService.WriteAsync(
            new TrustedMemoryOwner(session.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "Identity", "IDENTITY_MARKER", []),
            admission);
        await memoryService.PromoteToIdentityUserAsync(
            new TrustedMemoryOwner(session.SessionId),
            identity.MemoryId,
            new TrustedIdentityUserOwner(instance.InstanceId, ProfileId),
            promotionAllowed: true,
            admission);
        var identityScoped = await memoryService.FindActiveIdentityUserBySubjectAsync(
            new TrustedIdentityUserOwner(instance.InstanceId, ProfileId),
            MemoryKind.Fact,
            "Identity");
        Assert.NotNull(identityScoped);
        return new Harness(history, events, instance, identityScoped);
    }

    private sealed record Harness(
        AdminMemoryHistoryService History,
        IAdminEventStore Events,
        AgentInstance Instance,
        StructuredMemoryItem IdentityItem);

    private static DeterministicIdGenerator Ids(int count, string prefix) =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            [Guid.Parse("019944af-00e5-7000-8000-0000000000ff")]);

    private sealed class VersionedDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(id, definition.Id, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<AgentDefinition?>(null);
            }

            return version is { } exact && exact != definition.Version
                ? ValueTask.FromResult<AgentDefinition?>(null)
                : ValueTask.FromResult<AgentDefinition?>(definition);
        }
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
