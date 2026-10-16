using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_profiles_stay_disconnected_and_legacy_binding_cannot_consume_owned_secrets(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var inputs = new TenantCredentialCreate[]
        {
            new("Gmail", CredentialKind.Token, CredentialPurpose.EmailAccount, null, null, null, EmailAccountProfile.Gmail()),
            new("Drive", CredentialKind.Token, CredentialPurpose.GoogleDriveAccount, null, null, null),
            new("Custom", CredentialKind.Password, CredentialPurpose.EmailAccount, null, null, "custom-private-value", EmailAccountProfile.Custom("fixture@example.test")),
            new("Registry", CredentialKind.Token, CredentialPurpose.ContainerRegistry, null, null, "registry-private-value", ContainerRegistry: new("ghcr.io", "fixture", ["ghcr.io/acme"]))
        };
        var saved = new List<TenantCredentialView>();
        foreach (var input in inputs) saved.Add(await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, input, await Revision()));
        Assert.Equal(4, (await f.Service.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Total);
        AssertSafe(saved, "custom-private-value", "registry-private-value");
        var gmail = saved[0]; var drive = saved[1];
        Assert.Equal(EmailConnectionStatus.NotConnected, gmail.Credential.EmailAccount!.ConnectionStatus);
        Assert.Equal(DriveConnectionStatus.NotConnected, drive.Credential.DriveAccount!.ConnectionStatus);
        var revision = await Revision(); var audits = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        foreach (var invalid in new[]
        {
            inputs[0] with { ProtectedValue = "forged-oauth" },
            inputs[0] with { EmailAccount = EmailAccountProfile.Gmail("fixture@example.test") with { GoogleSubject = "forged", GrantedScopes = [EmailAccountProfile.ReadScope], AuthEpoch = 1, ConnectionStatus = EmailConnectionStatus.Connected } },
            inputs[2] with { EmailAccount = inputs[2].EmailAccount! with { Synthetic = true } },
            inputs[3] with { Kind = CredentialKind.Certificate }
        })
            await Assert.ThrowsAsync<AgentCoreException>(() => f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, invalid, revision).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Service.ReplaceAsync(owner.OwnerPrincipalId, owner.TenantId, gmail.Credential.CredentialId, "forged-oauth", revision, 1).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Service.ReplaceAsync(owner.OwnerPrincipalId, owner.TenantId, drive.Credential.CredentialId, "forged-oauth", revision, 1).AsTask());
        Assert.Equal(revision, await Revision()); Assert.Equal(audits, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        var custom = saved[2];
        await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, custom.Credential.CredentialId,
            new("Updated Custom", CredentialStatus.Disabled, null, null, EmailAccountProfile.Custom("updated@example.test")), revision, 1);
        var now = DateTimeOffset.UtcNow; var instance = new AgentInstance(f.Ids.NewId(), "legacy", 1, new("Legacy", "Test", "Test", "Clear"), AgentInstanceLifecycle.Active, now, now);
        await f.Instances.InsertAsync(instance);
        var binding = new AgentCredentialBinding(f.Ids.NewId(), instance.InstanceId, custom.Credential.CredentialId, "forged", 1, now, now);
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => ((IAgentCredentialBindingStore)f.Legacy).BindAsync(binding, 1).AsTask());
        Assert.Equal("tenant_context_required", denied.ReasonCode); Assert.Equal(1, (await f.Instances.FindAsync(instance.InstanceId))!.Revision);
        Assert.Empty(await ((IAgentCredentialBindingStore)f.Legacy).ListBindingsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_ownership_rejects_foreign_grants_and_cannot_adopt_existing_global_rows(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var created = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Owned", "private-value"), 1);
        var id = created.Credential.CredentialId; var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!;
        var scope = TenantCredentialRules.Scope(owner.TenantId, id);
        Assert.Equal(scope, IamResourceScopes.Create(owner.TenantId, "credential", id.ToString("N")));
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        var foreignScope = scope with { TenantId = beta.Tenant.TenantId };
        Assert.False((await f.Authorizer.CheckAsync(beta.Users.Single().PrincipalId, "credentials.read", foreignScope)).Allowed);
        var grant = new RoleAssignment(f.Ids.NewId(), beta.Tenant.TenantId, beta.Users.Single().PrincipalId,
            beta.Roles.Single(r => r.Key == "reader").RoleId, foreignScope, beta.Users.Single().PrincipalId, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Iam.TrySaveTenantAsync(beta with { Tenant = beta.Tenant with { Revision = beta.Tenant.Revision + 1 },
            Assignments = beta.Assignments.Append(grant).ToArray() }, beta.Tenant.Revision, f.Audit(beta.Tenant.TenantId, beta.Users.Single().PrincipalId, id)).AsTask());
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var legacy = current.Value with { CredentialId = f.Ids.NewId(), ProtectedPayload = "protected-fixture" };
        await f.Legacy.SaveAsync(legacy, 0);
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        Assert.False(await f.Credentials.TrySaveTenantCredentialAsync(next, state.Tenant.Revision,
            current.Owner with { CredentialId = legacy.CredentialId }, legacy, 0, 0, f.Audit(owner.TenantId, owner.OwnerPrincipalId, legacy.CredentialId)));
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.NotNull(await f.Legacy.GetAsync(legacy.CredentialId));
        Assert.Null(await f.Credentials.FindTenantCredentialAsync(owner.TenantId, legacy.CredentialId));
        Assert.False(await f.Iam.OwnsResourceAsync(TenantCredentialRules.Scope(owner.TenantId, legacy.CredentialId)));
    }
}
