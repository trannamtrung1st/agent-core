using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Baseline_resource_delete_disable_and_version_adoption_prune_membership_exclusions_in_the_owning_transaction(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("reconcile-owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var tenant = owner.TenantId; var actor = owner.OwnerPrincipalId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(actor, tenant, "Reconcile resource context", await Revision());
        var bound = await f.Service.BindResourcesAsync(actor, tenant, draft.DraftId, await Revision(), draft.Revision,
            [new(null, "a.txt", AgentDefinitionResourceKind.Reference, "text/plain", [65]), new(null, "b.txt", AgentDefinitionResourceKind.Reference, "text/plain", [66])]);
        await f.Service.PublishAsync(actor, tenant, draft.DraftId, await Revision(), bound.Revision);
        var (agents, configuration) = ConfigurationServices(f);
        var agent = await agents.CreateFromPublicationAsync(actor, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision());
        var instance = agent.Instance.InstanceId;
        async Task<long> InstanceRevision() => (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, instance))!.Instance.Revision;
        var localA = (await configuration.UpsertResourceAsync(actor, tenant, instance, await Revision(), await InstanceRevision(),
            new(null, null, "local-a.txt", AgentDefinitionResourceKind.Reference, "text/plain", [67]))).Resource!;
        var localB = (await configuration.UpsertResourceAsync(actor, tenant, instance, await Revision(), await InstanceRevision(),
            new(null, null, "local-b.txt", AgentDefinitionResourceKind.Reference, "text/plain", [68]))).Resource!;
        var inheritedA = bound.Resources.Single(r => r.LogicalPath == "a.txt"); var inheritedB = bound.Resources.Single(r => r.LogicalPath == "b.txt");
        var builtIns = new SessionNoBuiltinFallback();
        var resolver = new TenantRunConfigurationResolver((ITenantAgentStore)f.Iam, f.Definitions, builtIns,
            new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)f.Iam);
        var store = (ITenantSpaceStore)f.Iam;
        var spaces = new TenantSpaceService(f.Iam, store, (ITenantAgentStore)f.Iam, f.Authorizer, resolver,
            (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Ids, TimeProvider.System);
        var home = (await store.FindPrivateSpaceAsync(tenant, instance))!;
        var shared = await spaces.CreateAsync(actor, tenant, f.Ids.NewId(), "Shared resource context", "", "", await Revision());
        var keys = new[] { "InstanceResource:" + localA.Key, "InstanceResource:" + localB.Key,
            "DefinitionResource:definition:" + inheritedA.ResourceId.ToString("D"), "DefinitionResource:definition:" + inheritedB.ResourceId.ToString("D") };
        await spaces.SetMemberAsync(actor, tenant, home.SpaceId, instance, "Owner", "Keep contextual instructions", keys, [], await Revision(), home.Revision, 1);
        await spaces.SetMemberAsync(actor, tenant, shared.SpaceId, instance, "Reviewer", "Keep contextual instructions", keys, [], await Revision(), shared.Revision, 0);
        async Task Exclusions(params string[] remaining)
        {
            foreach (var space in new[] { home.SpaceId, shared.SpaceId })
            {
                var member = Assert.Single(await store.ReadMembershipsAsync(tenant, space));
                Assert.Equal(remaining.Order(StringComparer.Ordinal), member.DisabledInheritedBindingKeys.Order(StringComparer.Ordinal));
                Assert.Equal("Keep contextual instructions", member.Instructions); Assert.Empty(member.AddedBindings);
            }
        }
        await configuration.ChangeResourceAsync(actor, tenant, instance, localA.Key, true, null, await Revision(), await InstanceRevision(), localA.Revision);
        await Exclusions(keys[1], keys[2], keys[3]);
        await configuration.ChangeResourceAsync(actor, tenant, instance, localB.Key, false, false, await Revision(), await InstanceRevision(), localB.Revision);
        await Exclusions(keys[2], keys[3]);
        await configuration.ChangeResourceAsync(actor, tenant, instance, "definition:" + inheritedA.ResourceId.ToString("D"), false, false,
            await Revision(), await InstanceRevision(), 0);
        await Exclusions(keys[3]);
        var fork = await f.Service.ForkAsync(actor, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision());
        var removed = await f.Service.RemoveResourceAsync(actor, tenant, fork.DraftId, inheritedB.ResourceId, await Revision(), fork.Revision);
        await f.Service.PublishAsync(actor, tenant, fork.DraftId, await Revision(), removed.Revision);
        await configuration.AdoptVersionAsync(actor, tenant, instance, 2, await Revision(), await InstanceRevision());
        await Exclusions();
        Assert.Equal(8, (await f.Iam.ReadAuditAsync(tenant, 200)).Count(a => a.Action == "space-memberships.reconcile"));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            Assert.Empty(Assert.Single(await reopened.ReadMembershipsAsync(tenant, home.SpaceId)).DisabledInheritedBindingKeys);
            await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
        }
    }
}
