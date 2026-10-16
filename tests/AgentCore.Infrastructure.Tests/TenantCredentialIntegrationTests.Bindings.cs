using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_bindings_share_only_same_tenant_credentials_preserve_archive_and_reopen_and_never_grant_use(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Shared", "binding-private-sentinel"), await Revision());
        var a = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var b = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var id = credential.Credential.CredentialId;
        var first = await f.BindingService.BindAsync(owner.OwnerPrincipalId, owner.TenantId, a.Instance.InstanceId, id, "PRIMARY", await Revision(), 1, 1);
        var second = await f.BindingService.BindAsync(owner.OwnerPrincipalId, owner.TenantId, b.Instance.InstanceId, id, "shared", await Revision(), 1, 1);
        Assert.NotEqual(first.Binding.BindingId, second.Binding.BindingId);
        Assert.Equal("primary", first.Binding.Reference); Assert.Equal(2, first.InstanceRevision);
        var visible = await f.BindingService.ListAsync(owner.OwnerPrincipalId, owner.TenantId, a.Instance.InstanceId);
        Assert.Equal(2, visible.InstanceRevision);
        Assert.True(visible.AllRead); Assert.Equal(1, visible.Total); Assert.Equal("primary", Assert.Single(visible.Items).Reference);
        Assert.Equal(2, visible.Items[0].Credential.BindingCount); AssertSafe(visible, "binding-private-sentinel");
        Assert.Equal(2, (await f.Service.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Credential.BindingCount);
        var protectedValue = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Equal(1, protectedValue.Revision); Assert.Equal(0, protectedValue.SecretRevision);
        Assert.Equal("binding-private-sentinel", f.Protector.Unprotect(id, protectedValue.ProtectionVersion, protectedValue.ProtectedPayload));
        Assert.Empty(await ((IAgentCredentialBindingStore)f.Legacy).ListBindingsAsync());
        Assert.False((await f.Authorizer.CheckAsync(a.PrincipalId, "credentials.use", TenantCredentialRules.Scope(owner.TenantId, id))).Allowed);
        var legacyResolver = new CredentialService(f.Legacy, (IAgentCredentialBindingStore)f.Legacy, f.Protector, f.Instances, f.Ids, TimeProvider.System);
        Assert.Empty(await legacyResolver.SafeMetadataAsync(a.Instance.InstanceId));
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => legacyResolver.ResolvePasswordAsync(a.Instance.InstanceId, "primary", "https://example.test").AsTask());
        var before = await Revision(); var audits = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var blocked = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, id, before, 1).AsTask());
        Assert.Equal("credential_in_use", blocked.ReasonCode); Assert.Equal(before, await Revision());
        Assert.Equal(audits, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            Assert.Equal(first.Binding.BindingId, Assert.Single((await reopened.ReadTenantCredentialBindingsAsync(owner.TenantId, a.Instance.InstanceId))!.Bindings).Binding.BindingId);
            await using var db = f.Contexts!.CreateDbContext();
            Assert.All(await db.AgentCredentialBindings.ToArrayAsync(), row => Assert.Equal(owner.TenantId, row.TenantId));
        }
        credential = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, id,
            new("Disabled", CredentialStatus.Disabled, null, ["https://example.test"]), await Revision(), 1);
        var third = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        before = await Revision();
        var disabled = await Assert.ThrowsAsync<AccessDeniedException>(() => f.BindingService.BindAsync(owner.OwnerPrincipalId, owner.TenantId, third.Instance.InstanceId, id, "disabled", before, 1, 2).AsTask());
        Assert.Equal("credential_disabled", disabled.ReasonCode); Assert.Equal(before, await Revision());
        var removed = await f.BindingService.UnbindAsync(owner.OwnerPrincipalId, owner.TenantId, a.Instance.InstanceId, first.Binding.BindingId, await Revision(), 2, 1);
        Assert.Equal(3, removed.InstanceRevision);
        Assert.Equal(3, (await f.Agents.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, a.Instance.InstanceId)).Instance.Revision);
        Assert.Equal(0, (await f.BindingService.ListAsync(owner.OwnerPrincipalId, owner.TenantId, a.Instance.InstanceId)).Total);
        Assert.Equal(1, (await f.Service.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Credential.BindingCount);
        var archived = await f.Agents.SetLifecycleAsync(owner.OwnerPrincipalId, owner.TenantId, b.Instance.InstanceId, AgentInstanceLifecycle.Archived, await Revision(), 2);
        Assert.Equal(1, (await f.BindingService.ListAsync(owner.OwnerPrincipalId, owner.TenantId, b.Instance.InstanceId)).Total);
        before = await Revision();
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => f.BindingService.UnbindAsync(owner.OwnerPrincipalId, owner.TenantId, b.Instance.InstanceId, second.Binding.BindingId, before, archived.Instance.Revision, 1).AsTask());
        Assert.Equal("agent_archived", denied.ReasonCode);
        var restored = await f.Agents.SetLifecycleAsync(owner.OwnerPrincipalId, owner.TenantId, b.Instance.InstanceId, AgentInstanceLifecycle.Active, await Revision(), archived.Instance.Revision);
        await f.BindingService.UnbindAsync(owner.OwnerPrincipalId, owner.TenantId, b.Instance.InstanceId, second.Binding.BindingId, await Revision(), restored.Instance.Revision, 1);
        await f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), credential.Credential.Revision);
        Assert.Null(await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id));
        Assert.Empty((await ((ITenantCredentialBindingStore)f.Iam).ReadTenantCredentialBindingsAsync(owner.TenantId, b.Instance.InstanceId))!.Bindings);
        AssertSafe(await f.Iam.ReadAuditAsync(owner.TenantId, 200), "binding-private-sentinel");
    }
}
