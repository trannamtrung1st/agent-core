using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Providers;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_Session_resolves_owned_publication_and_independent_persona_without_global_definition_fallback(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Private Session definition", await Revision());
        var publication = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision);
        var builtIns = new SessionNoBuiltinFallback();
        var agents = new TenantAgentService(f.Iam, (ITenantAgentStore)f.Iam, builtIns, f.Authorizer, f.Ids, TimeProvider.System, ownedDefinitions: f.Definitions);
        var persona = new AgentIdentity("Independent persona", "Tester", "Private publication", "Clear");
        var agent = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(publication.DefinitionId), publication.Version, await Revision(), persona);
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, f.Definitions, builtIns, f.Identity, f.Authorizer, ModelCatalogFactory.Synthetic(), new(), f.Ids, TimeProvider.System);
        var item = await sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, new(owner.AccountId, login.SessionId, owner.OwnerPrincipalId), SessionMode.Text, await Revision(), 1);
        var snapshot = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, item.Owner.SessionId))!.Snapshot;
        Assert.DoesNotContain("Definition", System.Text.Json.JsonSerializer.Serialize(item));
        Assert.Equal(publication.DefinitionId, snapshot.Definition.Id); Assert.Equal(publication.Version, snapshot.Definition.Version);
        Assert.Equal(publication.Payload.AgentInstructions, snapshot.Definition.AgentInstructions); Assert.Equal(persona, snapshot.PinnedPersona);
        Assert.Equal(agent.PrincipalId, item.Owner.ExecutorPrincipalId); Assert.Null(snapshot.ProfileId);
        Assert.Null(await f.SessionStore.LoadAsync(item.Owner.SessionId));
        Assert.Equal(publication.Payload.AgentInstructions, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, item.Owner.SessionId))!.Snapshot.Definition.AgentInstructions);
    }
    private sealed class SessionNoBuiltinFallback : IBuiltInAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Owned Session must use its own publication.");
        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Owned Session must never use a global definition fallback.");
    }
}
