using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class IamIdentityIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_credential_binding_paths_exclude_owned_instances_and_preserve_their_rows(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var now = f.Time.GetUtcNow();
        var legacy = new AgentInstance(f.Ids.NewId(), "template", 1, new("Legacy", "Test", "Test", "Clear"), AgentInstanceLifecycle.Active, now, now);
        var owned = legacy with { InstanceId = f.Ids.NewId() };
        await f.Instances.InsertAsync(legacy); await f.Instances.InsertAsync(owned);
        ICredentialStore credentials = sqlite ? new SqliteCredentialStore(f.Contexts!) : new InMemoryCredentialStore((InMemoryAgentInstanceStore)f.Instances);
        var bindings = (IAgentCredentialBindingStore)credentials;
        var credential = new Credential(f.Ids.NewId(), "Shared fixture", CredentialKind.Password, CredentialStatus.Active,
            CredentialRules.Metadata(null), ["https://example.test"], "protected-fixture", 1, 1, now, now);
        await credentials.SaveAsync(credential, 0);
        var publicBinding = new AgentCredentialBinding(f.Ids.NewId(), legacy.InstanceId, credential.CredentialId, "public", 1, now, now);
        var privateBinding = publicBinding with { BindingId = f.Ids.NewId(), AgentInstanceId = owned.InstanceId, Reference = "private" };
        await bindings.BindAsync(publicBinding, 1); await bindings.BindAsync(privateBinding, 1);

        // Construct a valid owned graph with existing children, without implementing a migration.
        // Exclusion must follow the actual identity, independent of when a binding was created.
        var state = (await f.Store.ReadTenantAsync(owner.TenantId))!;
        var identityId = f.Ids.NewId(); var principalId = f.Ids.NewId();
        var next = state with
        {
            Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 },
            Principals = state.Principals.Append(new(principalId, PrincipalKind.SystemIdentity, owner.TenantId, null, null, identityId, IdentityStatus.Active)).ToArray(),
            SystemIdentities = state.SystemIdentities.Append(new(identityId, owner.TenantId, principalId, SystemIdentityKind.Agent, owned.InstanceId, IdentityStatus.Active)).ToArray()
        };
        Assert.True(await f.Store.TrySaveTenantAsync(next, state.Tenant.Revision,
            new(f.Ids.NewId(), owner.TenantId, owner.OwnerPrincipalId, "agents.create", owned.InstanceId.ToString("D"), "allowed", "fixture", now)));
        var revision = (await ((ITenantAgentStore)f.Store).FindTenantAgentAsync(owner.TenantId, owned.InstanceId))!.Instance.Revision;
        var auditBefore = (await f.Store.ReadAuditAsync(owner.TenantId, 200)).Count;
        Assert.Null(await f.Instances.FindAsync(owned.InstanceId));
        Assert.Equal(publicBinding, Assert.Single(await bindings.ListBindingsAsync()));
        Assert.Empty(await bindings.ListBindingsAsync(owned.InstanceId));

        async Task Denied(Func<Task> action)
        {
            var error = await Assert.ThrowsAsync<AccessDeniedException>(action);
            Assert.Equal("tenant_context_required", error.ReasonCode);
        }
        await Denied(() => bindings.BindAsync(privateBinding with { BindingId = f.Ids.NewId(), Reference = "forged" }, revision).AsTask());
        await Denied(() => bindings.UnbindAsync(owned.InstanceId, privateBinding.BindingId, 1, revision).AsTask());
        await Denied(() => bindings.DeleteBindingsAsync(owned.InstanceId).AsTask());
        Assert.Equal(revision, (await ((ITenantAgentStore)f.Store).FindTenantAgentAsync(owner.TenantId, owned.InstanceId))!.Instance.Revision);
        Assert.Equal(auditBefore, (await f.Store.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(next.Tenant.Revision, (await f.Store.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);

        var archived = await f.AgentService().SetLifecycleAsync(owner.OwnerPrincipalId, owner.TenantId, owned.InstanceId,
            AgentInstanceLifecycle.Archived, next.Tenant.Revision, revision);
        Assert.Equal(publicBinding, Assert.Single(await bindings.ListBindingsAsync()));
        Assert.Empty(await bindings.ListBindingsAsync(owned.InstanceId));
        await Denied(() => bindings.BindAsync(privateBinding with { BindingId = f.Ids.NewId() }, archived.Instance.Revision).AsTask());
        await Denied(() => bindings.UnbindAsync(owned.InstanceId, privateBinding.BindingId, 1, archived.Instance.Revision).AsTask());
        await Denied(() => bindings.DeleteBindingsAsync(owned.InstanceId).AsTask());
        Assert.True(await f.Instances.HasBackingInstanceAsync(owned.InstanceId));
        Assert.False(await f.Instances.HasBackingInstanceAsync(f.Ids.NewId()));

        // Legacy mutation and post-deletion cleanup still work; hidden bindings continue to
        // protect a shared credential from deletion, even though the global list excludes them.
        await bindings.UnbindAsync(legacy.InstanceId, publicBinding.BindingId, 1, 2);
        await bindings.BindAsync(publicBinding with { BindingId = f.Ids.NewId() }, 3);
        await bindings.DeleteBindingsAsync(legacy.InstanceId);
        await bindings.DeleteBindingsAsync(f.Ids.NewId());
        Assert.Empty(await bindings.ListBindingsAsync());
        var boundDelete = await Assert.ThrowsAsync<AgentCoreException>(() => credentials.DeleteAsync(credential.CredentialId, 1).AsTask());
        Assert.Equal(409, boundDelete.StatusCode);
        if (sqlite)
        {
            var reopened = new SqliteCredentialStore(f.Contexts!);
            Assert.Empty(await reopened.ListBindingsAsync());
            await using var db = f.Contexts!.CreateDbContext();
            var retained = Assert.Single(await db.AgentCredentialBindings.AsNoTracking().ToArrayAsync());
            Assert.Equal(privateBinding.BindingId.ToString("D"), retained.BindingId);
            Assert.Equal(1, retained.Revision);
        }
    }

}
