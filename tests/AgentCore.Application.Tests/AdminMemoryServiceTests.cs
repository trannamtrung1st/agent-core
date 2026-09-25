using AgentCore.Application.Admin;
using AgentCore.Application.Identity;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AdminMemoryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = LocalUserProfile.Id;

    [Fact]
    public async Task Reset_identity_user_scope_leaves_other_owners_unchanged()
    {
        var clock = new FakeTimeProvider(Now);
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
        var admin = new AdminMemoryService(
            instances,
            definitions,
            sessions,
            structured,
            memoryService,
            new FixedLocalProfile(ProfileId, clock));

        var instanceA = await instanceService.CreateAsync("examiner", 1);
        var instanceB = await instanceService.CreateAsync("examiner", 1);
        var sessionA = await manager.CreateForInstanceAsync(instanceA.InstanceId, SessionMode.Text);
        var sessionB = await manager.CreateForInstanceAsync(instanceB.InstanceId, SessionMode.Text);
        var admission = new MemoryAdmissionContext("test", [], new HashSet<string>(StringComparer.Ordinal));

        _ = await memoryService.WriteAsync(
            new TrustedMemoryOwner(sessionA.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "Session fact", "SESSION_SENTINEL", []),
            admission);
        var identityA = await memoryService.WriteAsync(
            new TrustedMemoryOwner(sessionA.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "Identity A", "IDENTITY_A", []),
            admission);
        await memoryService.PromoteToIdentityUserAsync(
            new TrustedMemoryOwner(sessionA.SessionId),
            identityA.MemoryId,
            new TrustedIdentityUserOwner(instanceA.InstanceId, ProfileId),
            promotionAllowed: true,
            admission);
        var identityB = await memoryService.WriteAsync(
            new TrustedMemoryOwner(sessionB.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "Identity B", "IDENTITY_B", []),
            admission);
        await memoryService.PromoteToIdentityUserAsync(
            new TrustedMemoryOwner(sessionB.SessionId),
            identityB.MemoryId,
            new TrustedIdentityUserOwner(instanceB.InstanceId, ProfileId),
            promotionAllowed: true,
            admission);
        var userItem = await memoryService.WriteAsync(
            new TrustedMemoryOwner(sessionA.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "User wide", "USER_SENTINEL", []),
            admission);
        await memoryService.PromoteSessionToUserAsync(
            new TrustedMemoryOwner(sessionA.SessionId),
            userItem.MemoryId,
            new TrustedUserOwner(ProfileId),
            promotionAllowed: true,
            admission);

        var reset = await admin.ResetScopeAsync(instanceA.InstanceId, AdminLearnedMemoryScope.IdentityUser, null);
        Assert.Equal(AdminLearnedMemoryScope.IdentityUser, reset.Scope);
        Assert.Equal(1, reset.ItemsRemoved);

        var sessionList = await admin.ListAsync(instanceA.InstanceId, AdminLearnedMemoryScope.Session, sessionA.SessionId);
        Assert.Contains(sessionList.Items, item => item.Content == "SESSION_SENTINEL");
        Assert.Contains(sessionList.Items, item => item.Content == "IDENTITY_A");

        var identityAList = await admin.ListAsync(instanceA.InstanceId, AdminLearnedMemoryScope.IdentityUser, null);
        Assert.Empty(identityAList.Items);

        var identityBList = await admin.ListAsync(instanceB.InstanceId, AdminLearnedMemoryScope.IdentityUser, null);
        Assert.Contains(identityBList.Items, item => item.Content == "IDENTITY_B");

        var userList = await admin.ListAsync(instanceA.InstanceId, AdminLearnedMemoryScope.User, null);
        Assert.Contains(userList.Items, item => item.Content == "USER_SENTINEL");

        var personaBefore = (await instances.FindAsync(instanceA.InstanceId))!.Persona.Name;
        var snapshot = await sessions.LoadAsync(sessionA.SessionId);
        Assert.NotNull(snapshot);
        Assert.Equal(definition.Id, snapshot!.Definition.Id);
        Assert.Equal(personaBefore, (await instances.FindAsync(instanceA.InstanceId))!.Persona.Name);
    }

    [Fact]
    public async Task Delete_user_scope_item_leaves_other_scopes_unchanged()
    {
        var clock = new FakeTimeProvider(Now);
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
        var instanceService = new AgentInstanceService(instances, definitions, sessions, Ids(8, "019944af-00c6-7000-8000-"), clock);
        var manager = new SessionManager(
            definitions,
            sessions,
            Ids(16, "019944af-00c7-7000-8000-"),
            clock,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            structuredMemory: structured,
            instances: instanceService);
        var memoryService = new StructuredMemoryService(structured, Ids(12, "019944af-00c8-7000-8000-"), clock);
        var admin = new AdminMemoryService(
            instances,
            definitions,
            sessions,
            structured,
            memoryService,
            new FixedLocalProfile(ProfileId, clock));

        var instance = await instanceService.CreateAsync("examiner", 1);
        var session = await manager.CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var admission = new MemoryAdmissionContext("test", [], new HashSet<string>(StringComparer.Ordinal));

        _ = await memoryService.WriteAsync(
            new TrustedMemoryOwner(session.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "Session fact", "SESSION_ONLY", []),
            admission);
        var identityItem = await memoryService.WriteAsync(
            new TrustedMemoryOwner(session.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "Identity fact", "IDENTITY_KEEP", []),
            admission);
        await memoryService.PromoteToIdentityUserAsync(
            new TrustedMemoryOwner(session.SessionId),
            identityItem.MemoryId,
            new TrustedIdentityUserOwner(instance.InstanceId, ProfileId),
            promotionAllowed: true,
            admission);
        var userKeepSource = await memoryService.WriteAsync(
            new TrustedMemoryOwner(session.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "User keep", "USER_KEEP", []),
            admission);
        _ = await memoryService.PromoteSessionToUserAsync(
            new TrustedMemoryOwner(session.SessionId),
            userKeepSource.MemoryId,
            new TrustedUserOwner(ProfileId),
            promotionAllowed: true,
            admission);
        var userDeleteSource = await memoryService.WriteAsync(
            new TrustedMemoryOwner(session.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "User delete", "USER_DELETE", []),
            admission);
        var userDelete = await memoryService.PromoteSessionToUserAsync(
            new TrustedMemoryOwner(session.SessionId),
            userDeleteSource.MemoryId,
            new TrustedUserOwner(ProfileId),
            promotionAllowed: true,
            admission);

        await admin.DeleteAsync(instance.InstanceId, AdminLearnedMemoryScope.User, userDelete.MemoryId, null);

        var sessionList = await admin.ListAsync(instance.InstanceId, AdminLearnedMemoryScope.Session, session.SessionId);
        Assert.Contains(sessionList.Items, item => item.Content == "SESSION_ONLY");
        Assert.Contains(sessionList.Items, item => item.Content == "IDENTITY_KEEP");

        var identityList = await admin.ListAsync(instance.InstanceId, AdminLearnedMemoryScope.IdentityUser, null);
        Assert.Contains(identityList.Items, item => item.Content == "IDENTITY_KEEP");

        var userList = await admin.ListAsync(instance.InstanceId, AdminLearnedMemoryScope.User, null);
        Assert.Contains(userList.Items, item => item.Content == "USER_KEEP");
        Assert.DoesNotContain(userList.Items, item => item.Content == "USER_DELETE");
        Assert.Single(userList.Items);
    }

    [Fact]
    public async Task List_identity_user_returns_empty_when_retrieval_disabled()
    {
        var clock = new FakeTimeProvider(Now);
        var sessions = new InMemoryMemoryStore();
        var structured = new InMemoryStructuredMemoryStore();
        var instances = new InMemoryAgentInstanceStore();
        var definition = SampleDefinitions.Examiner with
        {
            MemoryPolicy = new MemoryPolicy(SessionMemory: true, IdentityUserPromotion: true)
        };
        var definitions = new VersionedDefinitions(definition);
        var instanceService = new AgentInstanceService(instances, definitions, sessions, Ids(4, "019944af-00b6-7000-8000-"), clock);
        var admin = new AdminMemoryService(
            instances,
            definitions,
            sessions,
            structured,
            new StructuredMemoryService(structured, Ids(4, "019944af-00b8-7000-8000-"), clock),
            new FixedLocalProfile(ProfileId, clock));
        var instance = await instanceService.CreateAsync("examiner", 1);
        var list = await admin.ListAsync(instance.InstanceId, AdminLearnedMemoryScope.IdentityUser, null);
        Assert.Empty(list.Items);
    }

    private static DeterministicIdGenerator Ids(int count, string prefix) =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            [Guid.Parse("019944af-00b9-7000-8000-0000000000ff")]);

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
