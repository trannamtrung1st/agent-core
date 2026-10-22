using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    public static IEnumerable<object[]> ResourceUnchangedCases =>
        from sqlite in new[] { false, true }
        from operation in new[] { "content", "metadata", "local-choice", "inherited-reset", "inherited-choice", "inherited-default" }
        select new object[] { sqlite, operation };

    [Theory]
    [MemberData(nameof(ResourceUnchangedCases))]
    public async Task Resource_unchanged_commands_preserve_children_revisions_audit_and_immutable_bytes(bool sqlite, string operation)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var counted = new ResourcePreparationContent(f.Content);
        var (agents, configuration) = ConfigurationServices(f, content: counted);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var bytes = Encoding.UTF8.GetBytes("Immutable source evidence.");
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Resource preparation", await Revision());
        var bound = await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision,
            [new(null, "source.txt", AgentDefinitionResourceKind.Reference, "text/plain", bytes)]);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), bound.Revision);
        var agent = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), 1, await Revision());
        var id = agent.Instance.InstanceId; var store = (ITenantAgentStore)f.Iam;
        async ValueTask<long> Ir() => (await store.FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision;
        var local = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), await Ir(),
            new(null, null, "local.txt", AgentDefinitionResourceKind.Reference, "text/plain", bytes, false));
        var inherited = (await configuration.ReadResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Catalog.Resources.Single(r => r.Origin == "Definition");
        if (operation is "inherited-choice" or "inherited-default")
            inherited = (await configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, inherited.Key, false,
                operation == "inherited-default", await Revision(), await Ir(), 0)).Resource!;
        var before = (await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id))!;
        var beforeJson = JsonSerializer.Serialize(before); var revision = await Revision(); var audit = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        counted.Stores = 0; counted.Reads = 0;
        TenantAgentResourceSaved saved;
        if (operation is "content" or "metadata")
            saved = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, revision, before.Agent.Instance.Revision,
                new(Guid.Parse(local.Resource!.Key[9..]), 1, "local.txt", AgentDefinitionResourceKind.Reference, "text/plain", operation == "content" ? bytes : null));
        else
            saved = await configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id,
                operation == "local-choice" ? local.Resource!.Key : inherited.Key, false,
                operation == "inherited-reset" ? null : operation == "inherited-default", revision, before.Agent.Instance.Revision,
                operation == "local-choice" ? 1 : inherited.Revision);
        Assert.Equal(before.Agent.Instance.Revision, saved.InstanceRevision); Assert.Equal(revision, saved.SecurityRevision);
        Assert.Equal(revision, await Revision()); Assert.Equal(audit, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(beforeJson, JsonSerializer.Serialize(await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id)));
        Assert.Equal(0, counted.Stores); Assert.Equal(operation == "metadata" ? 1 : 0, counted.Reads);
        Assert.Equal(operation.StartsWith("inherited", StringComparison.Ordinal) ? inherited.Key : local.Resource!.Key, saved.Resource!.Key);
        Assert.Equal(bytes, (await configuration.ReadResourceContentAsync(owner.OwnerPrincipalId, owner.TenantId, id, saved.Resource.Key)).Bytes);
        if (sqlite)
        {
            var reopened = ConfigurationServices(f, new SqliteIamStore(f.Contexts!, f.Content)).Configuration;
            var current = (await reopened.ReadResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Catalog;
            Assert.Equal(saved.InstanceRevision, current.InstanceRevision);
            Assert.Equal(saved.Resource, current.Resources.Single(r => r.Key == saved.Resource.Key) with { Dependencies = saved.Resource.Dependencies });
        }
        // An unchanged value still requires the exact current child and parent witnesses.
        await Assert.ThrowsAsync<SecurityConflictException>(() => configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id,
            saved.Resource.Key, false, saved.Resource.EnabledOverride ?? saved.Resource.Enabled, revision, saved.InstanceRevision, saved.Resource.Revision + 1).AsTask());
        await Assert.ThrowsAsync<SecurityConflictException>(() => configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id,
            saved.Resource.Key, false, saved.Resource.Enabled, revision, saved.InstanceRevision + 1, saved.Resource.Revision).AsTask());
        Assert.Equal(revision, await Revision());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Resource_path_conflicts_fail_preparation_before_staging_blobs_or_committing_state(bool sqlite, bool rename)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var counted = new ResourcePreparationContent(f.Content); var (agents, configuration) = ConfigurationServices(f, content: counted);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision()); var id = agent.Instance.InstanceId;
        var first = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), 1,
            new(null, null, "occupied.txt", AgentDefinitionResourceKind.Reference, "text/plain", [65]));
        var second = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), first.InstanceRevision,
            new(null, null, "other.txt", AgentDefinitionResourceKind.Reference, "text/plain", [66]));
        var store = (ITenantAgentStore)f.Iam; var before = JsonSerializer.Serialize(await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id));
        var revision = await Revision(); var audit = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count; counted.Stores = 0;
        await Assert.ThrowsAsync<AgentCoreException>(() => configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, revision, second.InstanceRevision,
            new(rename ? Guid.Parse(second.Resource!.Key[9..]) : null, rename ? 1 : null, "occupied.txt", AgentDefinitionResourceKind.Reference, "text/plain", [67])).AsTask());
        Assert.Equal(0, counted.Stores); Assert.Equal(revision, await Revision()); Assert.Equal(audit, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(before, JsonSerializer.Serialize(await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id)));
    }

    private sealed class ResourcePreparationContent(IDefinitionResourceContentStore inner) : IDefinitionResourceContentStore
    {
        public int Reads, Stores;
        public ValueTask<byte[]?> ReadAsync(string hash, CancellationToken ct = default) { Reads++; return inner.ReadAsync(hash, ct); }
        public ValueTask StoreVerifiedAsync(string hash, byte[] bytes, CancellationToken ct = default) { Stores++; return inner.StoreVerifiedAsync(hash, bytes, ct); }
    }

    [Theory]
    [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "manage")] [InlineData(true, "manage")]
    [InlineData(false, "freeze")] [InlineData(true, "freeze")]
    public async Task Resource_unchanged_metadata_refuses_authority_changes_during_verified_byte_io(bool sqlite, string fence)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision()); var id = agent.Instance.InstanceId;
        var local = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), 1,
            new(null, null, "private.txt", AgentDefinitionResourceKind.Reference, "text/plain", [65]));
        await configuration.UpdateManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), local.InstanceRevision,
            new(new(HarnessManagementMode.Assisted), new([HarnessManagementScope.Resources])));
        async Task<RoleAssignment> Grant(string permission, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Preparation " + permission, [permission], await Revision());
            return await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, role.RoleId, scope, await Revision());
        }
        await Grant("agents.read", AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")));
        var child = TenantAgentChildScopes.Scope(owner.TenantId, id, "resource", local.Resource!.Key);
        var read = await Grant("resources.read", child); var manage = await Grant("resources.manage", child);
        var store = (ITenantAgentStore)f.Iam; var before = (await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id))!;
        var gated = new GatedInstanceContent(f.Content); var service = ConfigurationServices(f, content: gated).Configuration;
        var pending = service.UpsertResourceAsync(agent.PrincipalId, owner.TenantId, id, await Revision(), before.Agent.Instance.Revision,
            new(Guid.Parse(local.Resource.Key[9..]), 1, "private.txt", AgentDefinitionResourceKind.Reference, "text/plain", null)).AsTask();
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string captured; long revision; int audit;
        try
        {
            if (fence == "freeze")
                await configuration.FreezeManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), before.Agent.Instance.Revision);
            else await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, (fence == "read" ? read : manage).AssignmentId, await Revision());
            captured = JsonSerializer.Serialize(await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id));
            revision = await Revision(); audit = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        }
        finally { gated.Release.TrySetResult(); }
        if (fence == "freeze") await Assert.ThrowsAsync<SecurityConflictException>(() => pending);
        else await Assert.ThrowsAsync<AccessDeniedException>(() => pending);
        Assert.Equal(captured, JsonSerializer.Serialize(await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id)));
        Assert.Equal(revision, await Revision()); Assert.Equal(audit, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
    }
}
