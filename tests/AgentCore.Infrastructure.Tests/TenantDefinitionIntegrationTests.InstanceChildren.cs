using System.Text;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_instance_children_preserve_dependencies_choices_bytes_and_reopen_without_global_access(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Child dependencies", await Revision());
        var bytes = Encoding.UTF8.GetBytes("Owned reference evidence.");
        var bound = await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision,
            [new(null, "reference.txt", AgentDefinitionResourceKind.Reference, "text/plain", bytes)]);
        draft = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, draft.Candidate with { Skills = [
            new("review", "Review", "Review totals", "Compare the supplied totals.", SkillProjection.OnDemand, true, ["chat.respond"], []),
            new("evidence", "Evidence", "Review evidence", "Read the reference.", SkillProjection.OnDemand, true, ["chat.respond"], ["reference.txt"]) ] }, await Revision(), bound.Revision);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision);
        var agent = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), 1, await Revision());
        var id = agent.Instance.InstanceId; var store = (ITenantAgentStore)f.Iam;
        async ValueTask<long> Ir() => (await store.FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision;
        var customized = await configuration.WriteSkillAsync(owner.OwnerPrincipalId, owner.TenantId, id, "customize", await Revision(), await Ir(), "definition:review", 1);
        Assert.False(customized.DefinitionSkill!.Enabled); Assert.Equal("review", customized.Skill.SourceDefinitionSkillId);
        var source = Assert.Single((await configuration.ReadResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Catalog.Resources);
        var before = await Revision(); var parent = await Ir(); var audit = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        await Assert.ThrowsAsync<AgentCoreException>(() => configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, source.Key, false, false, before, parent, 0).AsTask());
        Assert.Equal(before, await Revision()); Assert.Equal(parent, await Ir()); Assert.Equal(audit, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        await configuration.WriteSkillAsync(owner.OwnerPrincipalId, owner.TenantId, id, "set_enabled", await Revision(), await Ir(), "definition:evidence", 1, enabled: false);
        await configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, source.Key, false, false, await Revision(), await Ir(), 0);
        await configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, source.Key, false, null, await Revision(), await Ir(), 1);
        var copied = await configuration.CopyResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, source.Key, "local/copy.txt", await Revision(), await Ir());
        Assert.NotEqual(source.Key, copied.Resource!.Key); Assert.Equal(draft.DefinitionId, copied.Resource.SourceDefinitionId);
        Assert.Equal(bytes, (await configuration.ReadResourceContentAsync(owner.OwnerPrincipalId, owner.TenantId, id, copied.Resource.Key)).Bytes);
        var local = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), await Ir(),
            new(null, null, "local/disabled.txt", AgentDefinitionResourceKind.Reference, "text/plain", bytes, false));
        var localId = Guid.Parse(local.Resource!.Key[9..]);
        var renamed = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), await Ir(),
            new(localId, 1, "local/renamed.txt", AgentDefinitionResourceKind.Reference, "text/plain", null));
        Assert.False(renamed.Resource!.Enabled); Assert.Equal(local.Resource.ContentSha256, renamed.Resource.ContentSha256);
        Assert.Null(await f.Instances.FindAsync(id)); Assert.Empty(await f.Instances.ListAsync(100)); Assert.Empty(await f.Instances.ListMaintenancePageAsync(null, 100));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Instances.ReadSkillsAsync(id).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Instances.ReadResourcesAsync(id).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Instances.MutateSkillsAsync(new(id, renamed.InstanceRevision)).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Instances.MutateResourcesAsync(new(id, renamed.InstanceRevision)).AsTask());
        if (sqlite)
        {
            var reopened = ConfigurationServices(f, new SqliteIamStore(f.Contexts!, f.Content)).Configuration;
            Assert.Equal(bytes, (await reopened.ReadResourceContentAsync(owner.OwnerPrincipalId, owner.TenantId, id, copied.Resource.Key)).Bytes);
            Assert.False((await reopened.ReadResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Catalog.Resources.Single(r => r.Key == renamed.Resource.Key).Enabled);
            Assert.Equal(customized.Skill.Procedure, (await reopened.ReadSkillsAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Items.Single(s => s.Key == customized.Skill.Key).Procedure);
        }
        await configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, renamed.Resource.Key, true, null, await Revision(), await Ir(), 2);
        Assert.Equal(bytes, await f.Content.ReadAsync(renamed.Resource.ContentSha256));
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.ReadResourceContentAsync(owner.OwnerPrincipalId, owner.TenantId, id, renamed.Resource.Key).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_instance_child_transactions_reject_malformed_cancelled_and_stale_writes_atomically(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, _) = ConfigurationServices(f);
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, 1); var id = agent.Instance.InstanceId;
        var store = (ITenantAgentStore)f.Iam; var before = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } }; var now = DateTimeOffset.UtcNow;
        var local = new AgentInstanceSkill("atomic-review", id, "Atomic review", "Check a transaction", "Compare totals.", SkillProjection.OnDemand, true, ["chat.respond"], 1, now, now, SkillAuthor.Admin);
        var mutation = new SkillMutation(id, 1, InstanceSkill: local);
        var change = new TenantAgentConfigurationChange(id, 1, Skill: mutation);
        AuthorizationAudit Audit() => new(f.Ids.NewId(), owner.TenantId, owner.OwnerPrincipalId, "skills.create", id.ToString("D"), "allowed", "test", now);
        var count = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        foreach (var invalid in new[] { mutation with { InstanceSkill = local with { AgentInstanceId = Guid.NewGuid() } }, mutation with { DeleteSkillId = "atomic-review" }, new(id, 1) })
            await Assert.ThrowsAsync<ArgumentException>(() => store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision, change with { Skill = invalid }, now, Audit()).AsTask());
        var forged = new AgentInstanceResource(id, Guid.NewGuid(), "forged.txt", AgentDefinitionResourceKind.Reference, "text/plain", new string('0', 64), 1, true, 1, now, now, SkillAuthor.Admin);
        foreach (var invalid in new[] { new InstanceResourceMutation(id, 1), new(id, 1, Resource: forged, DeleteResourceId: forged.ResourceId) })
            await Assert.ThrowsAsync<ArgumentException>(() => store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision,
                new(id, 1, Resource: invalid), now, Audit()).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision,
            new(id, 1, Resource: new(id, 1, Resource: forged)), now, Audit()).AsTask());
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision, change, now, Audit(), cancellation.Token).AsTask());
        }
        Assert.Empty((await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id))!.Skills.InstanceSkills);
        Assert.Empty((await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id))!.Resources.InstanceResources);
        Assert.Equal(count, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision, change, now, Audit()).AsTask()));
        Assert.Single(attempts, a => a is not null);
        var saved = (await store.ReadTenantAgentConfigurationAsync(owner.TenantId, id))!;
        var persisted = Assert.Single(saved.Skills.InstanceSkills);
        Assert.Equal(local.SkillId, persisted.SkillId); Assert.Equal(local.Procedure, persisted.Procedure); Assert.Equal(local.RequiredCapabilities, persisted.RequiredCapabilities);
        Assert.Equal(2, saved.Agent.Instance.Revision); Assert.Equal(next.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.Equal(count + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_instance_child_content_rechecks_revocation_after_private_byte_io(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, 1); var id = agent.Instance.InstanceId;
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var saved = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), 1,
            new(null, null, "protected.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("Protected bytes.")));
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var grant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, state.Roles.Single(r => r.Key == "contributor").RoleId, AccessScope.ForTenant(owner.TenantId), state.Tenant.Revision);
        var content = new GatedInstanceContent(f.Content);
        var reader = ConfigurationServices(f, content: content).Configuration;
        var pending = reader.ReadResourceContentAsync(agent.PrincipalId, owner.TenantId, id, saved.Resource!.Key).AsTask();
        await content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, grant.AssignmentId, await Revision()); }
        finally { content.Release.TrySetResult(); }
        await Assert.ThrowsAsync<AccessDeniedException>(() => pending);
        Assert.Equal("Protected bytes.", Encoding.UTF8.GetString((await configuration.ReadResourceContentAsync(owner.OwnerPrincipalId, owner.TenantId, id, saved.Resource.Key)).Bytes));
    }

    private sealed class GatedInstanceContent(IDefinitionResourceContentStore inner) : IDefinitionResourceContentStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask StoreVerifiedAsync(string hash, byte[] bytes, CancellationToken ct = default) => inner.StoreVerifiedAsync(hash, bytes, ct);
        public async ValueTask<byte[]?> ReadAsync(string hash, CancellationToken ct = default)
        {
            var bytes = await inner.ReadAsync(hash, ct); Entered.TrySetResult();
            await Release.Task.WaitAsync(ct); return bytes;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_instance_child_agent_writes_require_own_typed_iam_and_current_management_area(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, 1); var id = agent.Instance.InstanceId;
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        async ValueTask<long> Ir() => (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision;
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var grant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, state.Roles.Single(r => r.Key == "contributor").RoleId, AccessScope.ForTenant(owner.TenantId), state.Tenant.Revision);
        var input = new InstanceSkillInput("Agent review", "Review evidence", "Compare totals.", SkillProjection.OnDemand, true, ["chat.respond"]);
        var revision = await Revision(); var parent = await Ir();
        await Assert.ThrowsAsync<AgentCoreException>(() => configuration.WriteSkillAsync(agent.PrincipalId, owner.TenantId, id, "create", revision, parent, input: input).AsTask());
        await configuration.UpdateManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), await Ir(),
            new(new(HarnessManagementMode.Assisted), new([HarnessManagementScope.Skills])));
        var saved = await configuration.WriteSkillAsync(agent.PrincipalId, owner.TenantId, id, "create", await Revision(), await Ir(), input: input);
        Assert.Equal(SkillAuthor.Agent, saved.Skill.CreatedBy);
        revision = await Revision(); parent = await Ir();
        await Assert.ThrowsAsync<AgentCoreException>(() => configuration.UpsertResourceAsync(agent.PrincipalId, owner.TenantId, id, revision, parent,
            new(null, null, "agent.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("Agent bytes."))).AsTask());
        await configuration.FreezeManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), await Ir());
        revision = await Revision(); parent = await Ir();
        await Assert.ThrowsAsync<AgentCoreException>(() => configuration.WriteSkillAsync(agent.PrincipalId, owner.TenantId, id, "delete", revision, parent, saved.Skill.Key, 1).AsTask());
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, grant.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.ReadSkillsAsync(agent.PrincipalId, owner.TenantId, id).AsTask());
        Assert.Contains((await configuration.ReadSkillsAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Items, s => s.Key == saved.Skill.Key);
    }
}
