using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private static TenantSandboxProfileInput SandboxInput(string name = "Private profile") => new(name, "Synthetic catalog evidence", true,
        "ghcr.io/acme/tool@sha256:" + new string('a', 64));
    private static TenantSandboxProfileService SandboxService(Fixture f, ITenantSandboxProfileStore? profiles = null, ISandboxImageAuthority? images = null) =>
        new(f.Iam, profiles ?? (ITenantSandboxProfileStore)f.Iam, f.Identity, f.Authorizer,
            images ?? new SandboxImageAuthority(new(), f.Sandboxes, f.Clock, f.Protector, f.Credentials, new TenantSandboxImageAuthorization(f.Iam, (ITenantSandboxProfileStore)f.Iam, f.Identity, f.Authorizer)), f.Ids, f.Clock);
    private static async Task<HumanSessionReference> SandboxHuman(Fixture f, Guid principal, string login = "owner@example.test", string password = "strong-owner-password")
    {
        var signed = (await f.Identity.SignInAsync(login, password))!;
        return new(signed.Account.AccountId, signed.Session.SessionId, principal);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Sandbox_profile_ownership_history_CAS_global_fences_and_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var service = SandboxService(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var first = await service.SaveAsync(owner.TenantId, null, human, SandboxInput(), await Revision(), 0);
        var id = first.Profile.Id; var scope = TenantSandboxProfileRules.Scope(owner.TenantId, id);
        Assert.Equal(owner.TenantId, first.Profile.TenantId); Assert.True(await f.Iam.OwnsResourceAsync(scope));
        Assert.Null(await f.Sandboxes.GetProfileAsync(id)); Assert.Empty(await f.Sandboxes.ListProfilesAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sandboxes.SaveProfileAsync(first.Profile with { Revision = 2 }, 1).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sandboxes.SaveProfileAsync(first.Profile with { TenantId = null, Revision = 2 }, 1).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => f.Sandboxes.SaveVerificationAsync(new(id, 1, f.Clock.GetUtcNow(), true, "Forged global verification")).AsTask());
        Assert.Null(await f.Sandboxes.GetVerificationAsync(id, 1));
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        var betaHuman = human with { PrincipalId = beta.Users.Single().PrincipalId };
        Assert.False(await f.Iam.OwnsResourceAsync(scope with { TenantId = beta.Tenant.TenantId }));
        Assert.Equal(0, (await service.ListAsync(beta.Tenant.TenantId, betaHuman)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(beta.Tenant.TenantId, id, betaHuman).AsTask());
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var store = (ITenantSandboxProfileStore)f.Iam;
        Task<bool> Save(string name) => store.TrySaveTenantSandboxProfileAsync(next, state.Tenant.Revision, new(owner.TenantId, id, f.Clock.GetUtcNow()),
            first.Profile with { Name = name, Revision = 2 }, 1, human, new(f.Ids.NewId(), owner.TenantId, human.PrincipalId, "sandbox.profile.save", id, "allowed", "fixture", f.Clock.GetUtcNow())).AsTask();
        var outcomes = await Task.WhenAll(Task.Run(() => Save("Racer A")), Task.Run(() => Save("Racer B"))); Assert.Single(outcomes, x => x);
        Assert.Equal(2, (await service.ReadAsync(owner.TenantId, id, human)).Profile.Revision);
        Assert.Equal("Private profile", (await service.ReadAsync(owner.TenantId, id, human, 1)).Profile.Name);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.SaveAsync(owner.TenantId, id, human, SandboxInput(), expectedSecurityRevision: 0, expectedProfileRevision: 1).AsTask());
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            Assert.Equal(2, (await reopened.FindTenantSandboxProfileAsync(owner.TenantId, id))!.Revision);
            await using var db = f.Contexts!.CreateDbContext();
            db.Add(new SandboxProfileRow { Id = id, Revision = 3, TenantId = beta.Tenant.TenantId, Json = System.Text.Json.JsonSerializer.Serialize(first.Profile with { Revision = 3, TenantId = beta.Tenant.TenantId }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            db.Add(new SandboxProfileRow { Id = id, Revision = 3, TenantId = owner.TenantId, Json = "{\"id\":\"" + id + "\",\"revision\":3}" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Sandbox_profile_exact_grants_filter_before_search_count_and_paging_without_execution_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var service = SandboxService(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var first = await service.SaveAsync(owner.TenantId, null, human, SandboxInput("Readable — RÉSUMÉ_100%"), await Revision(), 0);
        var second = await service.SaveAsync(owner.TenantId, null, human, SandboxInput("Hidden"), await Revision(), 0);
        var user = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "agent-operator");
        var reader = await SandboxHuman(f, user.User.PrincipalId, "writer@example.test", "strong-writer-password");
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "One profile", ["sandbox.read", "sandbox.manage"], await Revision());
        var scope = TenantSandboxProfileRules.Scope(owner.TenantId, first.Profile.Id);
        var assignment = await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, reader.PrincipalId, role.RoleId, scope, await Revision());
        var page = await service.ListAsync(owner.TenantId, reader, 0, 1); Assert.Equal(1, page.Total); Assert.False(page.AllReadable); Assert.False(page.CanCreate);
        Assert.Equal(first.Profile.Id, Assert.Single(page.Items).Profile.Id); Assert.True(page.Items[0].CanManage);
        Assert.Equal(0, (await service.ListAsync(owner.TenantId, reader, search: "Hidden")).Total);
        Assert.Equal(1, (await service.ListAsync(owner.TenantId, reader, search: "résumé_100%")).Total);
        Assert.Empty((await service.ListAsync(owner.TenantId, reader, 1, 1)).Items);
        Assert.False((await f.Authorizer.CheckAsync(reader.PrincipalId, "sandbox.execute", scope)).Allowed);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(owner.TenantId, second.Profile.Id, reader).AsTask());
        var beforeCreate = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SaveAsync(owner.TenantId, null, reader, SandboxInput(), beforeCreate, 0).AsTask());
        await service.SaveAsync(owner.TenantId, first.Profile.Id, reader, SandboxInput("Updated exact profile"), await Revision(), 1);
        var registry = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId, new("Registry", CredentialKind.Token, CredentialPurpose.ContainerRegistry,
            null, null, "private-registry-sentinel", ContainerRegistry: new("ghcr.io", "fixture", ["ghcr.io/acme"])), await Revision());
        var privateInput = SandboxInput() with { RegistryCredentialId = registry.Credential.CredentialId };
        var sourceRole = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Read and bind source", ["credentials.read", "credentials.bind"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, reader.PrincipalId, sourceRole.RoleId, TenantCredentialRules.Scope(owner.TenantId, registry.Credential.CredentialId), await Revision());
        var noUseRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SaveAsync(owner.TenantId, first.Profile.Id, reader, privateInput, noUseRevision, 2).AsTask());
        var useRole = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Use source", ["credentials.use"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, reader.PrincipalId, useRole.RoleId, TenantCredentialRules.Scope(owner.TenantId, registry.Credential.CredentialId), await Revision());
        Assert.Equal(3, (await service.SaveAsync(owner.TenantId, first.Profile.Id, reader, privateInput, await Revision(), 2)).Profile.Revision);
        await f.Access.RevokeAssignmentAsync(human.PrincipalId, owner.TenantId, assignment.AssignmentId, await Revision());
        Assert.Equal(0, (await service.ListAsync(owner.TenantId, reader)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SaveAsync(owner.TenantId, first.Profile.Id, reader, SandboxInput(), 1, 1).AsTask());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Sandbox_profile_original_human_is_rechecked_after_reads_and_at_atomic_commit(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var service = SandboxService(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var first = await service.SaveAsync(owner.TenantId, null, human, SandboxInput(), await Revision(), 0);
        var hook = new SandboxStoreHook((ITenantSandboxProfileStore)f.Iam);
        var captured = human; hook.AfterRead = async () => { await f.Identity.SignOutAsync(captured.AuthenticationSessionId); _ = await SandboxHuman(f, owner.OwnerPrincipalId); };
        await Assert.ThrowsAsync<AccessDeniedException>(() => SandboxService(f, hook).ReadAsync(owner.TenantId, first.Profile.Id, human).AsTask());
        human = await SandboxHuman(f, owner.OwnerPrincipalId); captured = human; hook.AfterRead = null;
        hook.BeforeWrite = async () => { await f.Identity.SignOutAsync(captured.AuthenticationSessionId); _ = await SandboxHuman(f, owner.OwnerPrincipalId); };
        var before = await Revision(); var auditCount = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => SandboxService(f, hook).SaveAsync(owner.TenantId, first.Profile.Id, human, SandboxInput("Must not commit"), before, 1).AsTask());
        Assert.Equal("initiator_unavailable", denied.ReasonCode);
        Assert.Equal(before, await Revision()); Assert.Equal(auditCount, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(1, (await ((ITenantSandboxProfileStore)f.Iam).FindTenantSandboxProfileAsync(owner.TenantId, first.Profile.Id))!.Revision);
        human = await SandboxHuman(f, owner.OwnerPrincipalId);
        Assert.Equal(2, (await service.SaveAsync(owner.TenantId, first.Profile.Id, human, SandboxInput("Explicit recovery"), before, 1)).Profile.Revision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Sandbox_profile_registry_uses_owned_credentials_and_fences_epochs_while_allowing_safe_disable(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var service = SandboxService(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId, new("Registry", CredentialKind.Token, CredentialPurpose.ContainerRegistry,
            null, null, "private-registry-sentinel", ContainerRegistry: new("ghcr.io", "fixture", ["ghcr.io/acme"])), await Revision());
        var input = SandboxInput() with { RegistryCredentialId = credential.Credential.CredentialId };
        var first = await service.SaveAsync(owner.TenantId, null, human, input, await Revision(), 0); var id = first.Profile.Id;
        Assert.Equal(0, first.Profile.RegistrySecretRevision); Assert.Null(await f.Legacy.GetAsync(credential.Credential.CredentialId));
        var authority = new SandboxImageAuthority(new(), f.Sandboxes, f.Clock, f.Protector, f.Credentials);
        await Assert.ThrowsAsync<AccessDeniedException>(() => authority.ResolveAuthAsync(first.Profile, CancellationToken.None, false).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => authority.ResolveAsync(first.Profile).AsTask());
        Assert.False((await authority.InspectAsync(first.Profile)).Eligible);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(); var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        var betaHuman = human with { PrincipalId = beta.Users.Single().PrincipalId };
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SaveAsync(beta.Tenant.TenantId, null, betaHuman, input, beta.Tenant.Revision, 0).AsTask());
        await f.Service.ReplaceAsync(human.PrincipalId, owner.TenantId, credential.Credential.CredentialId, "rotated-registry-sentinel", await Revision(), 1);
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        await Assert.ThrowsAsync<SecurityConflictException>(() => ((ITenantSandboxProfileStore)f.Iam).TrySaveTenantSandboxProfileAsync(next, state.Tenant.Revision,
            new(owner.TenantId, id, f.Clock.GetUtcNow()), first.Profile with { Revision = 2 }, 1, human,
            new(f.Ids.NewId(), owner.TenantId, human.PrincipalId, "sandbox.profile.save", id, "allowed", "fixture", f.Clock.GetUtcNow())).AsTask());
        Assert.Equal(state.Tenant.Revision, await Revision());
        await f.Service.DeleteAsync(human.PrincipalId, owner.TenantId, credential.Credential.CredentialId, await Revision(), 2);
        Assert.Equal("SourceUnavailable", (await authority.InspectAsync(first.Profile)).Status);
        var disabled = await service.SaveAsync(owner.TenantId, id, human, input with { Enabled = false }, await Revision(), 1);
        Assert.False(disabled.Profile.Enabled); Assert.Equal(first.Profile.RegistrySecretRevision, disabled.Profile.RegistrySecretRevision);
        AssertSafe(await f.Iam.ReadAuditAsync(owner.TenantId, 200), "private-registry-sentinel", "rotated-registry-sentinel");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Sandbox_profile_tag_preparation_rechecks_original_human_before_protected_registry_dispatch(bool sqlite)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        await using var registry = builder.Build(); registry.Urls.Add("http://127.0.0.1:0");
        var calls = 0; var authorization = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("fixture:private-registry-sentinel"));
        registry.MapMethods("/v2/acme/tool/manifests/latest", ["HEAD"], (HttpContext http) =>
        {
            Assert.Equal(authorization, http.Request.Headers.Authorization); Interlocked.Increment(ref calls);
            http.Response.Headers["Docker-Content-Digest"] = "sha256:" + new string('b', 64);
        });
        await registry.StartAsync(); var host = new Uri(registry.Urls.Single()).Authority;
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId, new("Registry", CredentialKind.Token, CredentialPurpose.ContainerRegistry,
            null, null, "private-registry-sentinel", ContainerRegistry: new(host, "fixture", [host + "/acme"])), await Revision());
        var options = new OpenSandboxOptions { AllowedImagePrefixes = [host + "/acme"], InsecureRegistryHosts = [host] };
        var guard = new TenantSandboxImageAuthorization(f.Iam, (ITenantSandboxProfileStore)f.Iam, f.Identity, f.Authorizer);
        var images = new SandboxImageAuthority(options, f.Sandboxes, f.Clock, f.Protector, f.Credentials, guard);
        var service = SandboxService(f, images: images);
        var input = SandboxInput() with { ImageReference = host + "/acme/tool:latest", RegistryCredentialId = credential.Credential.CredentialId };
        var first = await service.SaveAsync(owner.TenantId, null, human, input, await Revision(), 0);
        Assert.Equal(host + "/acme/tool@sha256:" + new string('b', 64), first.Profile.ImageReference); Assert.Equal(1, calls);
        var captured = human; var lookups = 0;
        var hookedCredentials = new SandboxCredentialHook(f.Credentials, async () =>
        {
            if (++lookups == 2) { await f.Identity.SignOutAsync(captured.AuthenticationSessionId); _ = await SandboxHuman(f, owner.OwnerPrincipalId); }
        });
        var hookedImages = new SandboxImageAuthority(options, f.Sandboxes, f.Clock, f.Protector, hookedCredentials, guard);
        var before = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => SandboxService(f, images: hookedImages).SaveAsync(owner.TenantId, first.Profile.Id, human, input, before, 1).AsTask());
        Assert.Equal(1, calls); Assert.Equal(before, await Revision()); Assert.Equal(1, (await ((ITenantSandboxProfileStore)f.Iam).FindTenantSandboxProfileAsync(owner.TenantId, first.Profile.Id))!.Revision);
        human = await SandboxHuman(f, owner.OwnerPrincipalId);
        Assert.Equal(2, (await service.SaveAsync(owner.TenantId, first.Profile.Id, human, input, before, 1)).Profile.Revision); Assert.Equal(2, calls);
        await registry.StopAsync();
    }

    private sealed class SandboxCredentialHook(ITenantCredentialStore inner, Func<Task> afterRead) : ITenantCredentialStore
    {
        public ValueTask<IReadOnlyDictionary<Guid, DateTimeOffset>> ReadTenantCredentialRotationsAsync(Guid tenant, IReadOnlyList<Guid> ids, CancellationToken ct = default)
            => inner.ReadTenantCredentialRotationsAsync(tenant, ids, ct);
        public async ValueTask<TenantCredential?> FindTenantCredentialAsync(Guid tenant, Guid id, CancellationToken ct = default)
        { var value = await inner.FindTenantCredentialAsync(tenant, id, ct); await afterRead(); return value; }
        public ValueTask<TenantCredentialPage> ListTenantCredentialsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default) => inner.ListTenantCredentialsAsync(tenantId, allowedIds, page, pageSize, ct);
        public ValueTask<bool> TrySaveTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, TenantCredentialRoot owner, Credential value, long expectedRevision, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) => inner.TrySaveTenantCredentialAsync(next, expectedSecurityRevision, owner, value, expectedRevision, expectedSecretRevision, audit, ct, initiator);
        public ValueTask<bool> TryDeleteTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credentialId, long expectedRevision, long expectedSecretRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) => inner.TryDeleteTenantCredentialAsync(next, expectedSecurityRevision, credentialId, expectedRevision, expectedSecretRevision, at, audit, ct, initiator);
    }

    private sealed class SandboxStoreHook(ITenantSandboxProfileStore inner) : ITenantSandboxProfileStore
    {
        public Func<Task>? AfterRead { get; set; }
        public Func<Task>? BeforeWrite { get; set; }
        public async ValueTask<SandboxProfile?> FindTenantSandboxProfileAsync(Guid tenant, string id, long? revision = null, CancellationToken ct = default)
        { var value = await inner.FindTenantSandboxProfileAsync(tenant, id, revision, ct); if (AfterRead is { } read) await read(); return value; }
        public async ValueTask<TenantSandboxProfilePage> ListTenantSandboxProfilesAsync(Guid tenant, IReadOnlyList<string>? allowedIds, int page, int pageSize, string search, CancellationToken ct = default)
        { var value = await inner.ListTenantSandboxProfilesAsync(tenant, allowedIds, page, pageSize, search, ct); if (AfterRead is { } read) await read(); return value; }
        public async ValueTask<bool> TrySaveTenantSandboxProfileAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSandboxProfileRoot root, SandboxProfile profile,
            long expectedProfileRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
        { if (BeforeWrite is { } write) await write(); return await inner.TrySaveTenantSandboxProfileAsync(next, expectedSecurityRevision, root, profile, expectedProfileRevision, human, audit, ct); }
    }
}
