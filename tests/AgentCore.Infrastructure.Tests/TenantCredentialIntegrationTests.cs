using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_credential_lifecycle_protects_values_and_never_reopens_global_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var created = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Private", "first-private-value"), await Revision());
        var id = created.Credential.CredentialId; var scope = TenantCredentialRules.Scope(owner.TenantId, id);
        Assert.Null(await f.Legacy.GetAsync(id)); Assert.Empty(await f.Legacy.ListAsync());
        Assert.True(await f.Iam.OwnsResourceAsync(scope));
        Assert.False(await f.Iam.OwnsResourceAsync(scope with { TenantId = beta.Tenant.TenantId }));
        Assert.Equal(0, (await f.Service.ListAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.ReadAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId, id).AsTask());
        AssertSafe(created, "first-private-value");
        var backing = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!;
        Assert.Equal("first-private-value", f.Protector.Unprotect(id, backing.Value.ProtectionVersion, backing.Value.ProtectedPayload));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Legacy.SaveAsync(backing.Value with { Revision = 2 }, 1).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Legacy.DeleteAsync(id, 1).AsTask());
        Assert.False(await f.Legacy.TryRotateOAuthSecretAsync(id, 1, 0, "forged"));
        var replaced = await f.Service.ReplaceAsync(owner.OwnerPrincipalId, owner.TenantId, id, "second-private-value", await Revision(), 1);
        Assert.Equal(2, replaced.Credential.Revision); AssertSafe(replaced, "first-private-value", "second-private-value");
        var currentRevision = await Revision();
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.ReplaceAsync(owner.OwnerPrincipalId, owner.TenantId, id, "stale", currentRevision, 1).AsTask());
        var updated = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, id, new("Renamed", CredentialStatus.Disabled, null, ["https://example.test"]), await Revision(), 2);
        Assert.Equal("Disabled", updated.Credential.Status);
        Assert.Equal("Renamed", Assert.Single((await f.Service.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Items).Credential.DisplayName);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            var retained = (await reopened.FindTenantCredentialAsync(owner.TenantId, id))!;
            var protector = new LocalCredentialProtector(Path.Combine(f.Root, "keys"));
            Assert.Equal("second-private-value", protector.Unprotect(id, retained.Value.ProtectionVersion, retained.Value.ProtectedPayload));
        }
        await f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), 3);
        Assert.False(await f.Iam.OwnsResourceAsync(scope));
        Assert.Null(await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id));
        Assert.Equal(0, (await f.Service.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Legacy.SaveAsync(backing.Value, 0).AsTask());
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.False(await f.Credentials.TrySaveTenantCredentialAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            backing.Owner, backing.Value, 0, 0, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
        AssertSafe(await f.Iam.ReadAuditAsync(owner.TenantId, 200), "first-private-value", "second-private-value");
        if (sqlite)
        {
            Assert.Null(await new SqliteIamStore(f.Contexts!).FindTenantCredentialAsync(owner.TenantId, id));
            await using var db = f.Contexts!.CreateDbContext();
            Assert.NotNull((await db.Set<TenantCredentialRecord>().SingleAsync(r => r.CredentialId == id.ToString("D"))).DeletedAt);
            Assert.False(await db.Credentials.AnyAsync(c => c.CredentialId == id.ToString("D")));
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(f.Root, "*.db*"))
            {
                var bytes = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
                Assert.DoesNotContain("first-private-value", bytes); Assert.DoesNotContain("second-private-value", bytes);
            }
        }
    }
    private static TenantCredentialCreate Password(string name, string value) => new(name, CredentialKind.Password, CredentialPurpose.General,
        new Dictionary<string, string> { ["username"] = "fixture@example.test" }, ["https://example.test"], value);
    private static void AssertSafe(object value, params string[] secrets)
    {
        var json = JsonSerializer.Serialize(value);
        foreach (var secret in secrets) Assert.DoesNotContain(secret, json);
        foreach (var field in new[] { "ProtectedPayload", "ProtectionVersion", "SecretRevision", "ProtectedValue" }) Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
    }
    internal sealed class Fixture(TimeProvider? clock = null) : IAsyncDisposable
    {
        public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tenant-credentials-" + Guid.NewGuid().ToString("N"));
        public IIamStore Iam { get; private set; } = null!;
        public ITenantCredentialStore Credentials => (ITenantCredentialStore)Iam;
        public ICredentialStore Legacy { get; private set; } = null!;
        public IAgentInstanceStore Instances { get; private set; } = null!;
        public IMemoryStore Memory { get; private set; } = null!;
        public TenantSessionService Sessions => SessionService((ITenantSessionStore)Iam);
        public TenantSessionService SessionService(ITenantSessionStore sessions) => new(Iam, (ITenantAgentStore)Iam, sessions, (ITenantRunStore)Iam, (ITenantDefinitionStore)Iam, BuiltIns,
            Identity, Authorizer, ModelCatalogFactory.Create("Synthetic", null, null), new(), Ids, Clock);
        public IDefinitionResourceContentStore RunContent { get; } = new InMemoryDefinitionResourceContentStore();
        public TenantRunService RunService(ITenantRunStore? runs = null) => new(Iam, (ITenantSessionStore)Iam, runs ?? (ITenantRunStore)Iam,
            RunConfiguration(),
            Identity, Authorizer, ModelCatalogFactory.Synthetic(), Ids, Clock);
        public TenantRunConfigurationResolver RunConfiguration() => new((ITenantAgentStore)Iam, (ITenantDefinitionStore)Iam, BuiltIns, RunResourceCatalog(), (ITenantCredentialBindingStore)Iam);
        public IIdGenerator Ids { get; } = new SystemIdGenerator(TimeProvider.System);
        public IIdentityTokens Tokens { get; } = new IdentityTokens();
        public IamAuthorizer Authorizer => new(Iam);
        public IdentityService Identity => new(Iam, new AccountPasswordHasher(), Tokens, Ids, Clock);
        public TenantAccessService Access => new(Iam, Authorizer, Identity, Tokens, Ids, Clock);
        public LocalCredentialProtector Protector => new(Path.Combine(Root, "keys"));
        public TenantCredentialService Service => new(Iam, Credentials, Authorizer, Protector, Ids, Clock);
        public TenantCredentialBindingService BindingService => new(Iam, (ITenantAgentStore)Iam, Credentials, (ITenantCredentialBindingStore)Iam, Authorizer,
            BuiltIns, (ITenantDefinitionStore)Iam, Ids, TimeProvider.System);
        public TenantAgentService Agents => new(Iam, (ITenantAgentStore)Iam, BuiltIns, Authorizer, Ids, TimeProvider.System, ownedDefinitions: (ITenantDefinitionStore)Iam);
        private IBuiltInAgentDefinitionStore? _builtIns;
        private AgentCore.Application.Agents.InheritedDefinitionResourceCatalog RunResourceCatalog()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
            var root = Path.Combine(directory!.FullName, "agents");
            return new(new InMemoryAgentDefinitionResourceAdminStore(new InMemoryAgentDefinitionAdminStore(Ids), RunContent, Ids),
                BuiltIns, new FileApprovedKnowledgeCatalog(root), RunContent);
        }
        private IBuiltInAgentDefinitionStore BuiltIns => _builtIns ??= LoadBuiltIns();
        private static IBuiltInAgentDefinitionStore LoadBuiltIns()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
            return new FileAgentDefinitionStore(Path.Combine(directory!.FullName, "agents"), SyntheticProviderAliases.Default);
        }
        public IDbContextFactory<AgentCoreDbContext>? Contexts { get; private set; }
        public static async Task<Fixture> Create(bool sqlite, TimeProvider? clock = null)
        {
            var f = new Fixture(clock); Directory.CreateDirectory(f.Root);
            try
            {
                if (sqlite)
                {
                    f.Contexts = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={f.Root}/store.db;Foreign Keys=True;Default Timeout=10").Options);
                    await using (var db = f.Contexts.CreateDbContext()) { await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges()); }
                    f.Iam = new SqliteIamStore(f.Contexts, clock: f.Clock); f.Legacy = new SqliteCredentialStore(f.Contexts); f.Instances = new SqliteAgentInstanceStore(f.Contexts, f.Ids);
                    f.Memory = new SqliteMemoryStore(f.Contexts, f.Clock);
                }
                else
                {
                    var instances = new InMemoryAgentInstanceStore(); var credentials = new InMemoryCredentialStore(instances);
                    f.Legacy = credentials; f.Instances = instances;
                    var memory = new InMemoryMemoryStore(instances); f.Memory = memory;
                    f.Iam = new InMemoryIamStore(instances, new InMemoryAgentDefinitionAdminStore(f.Ids), memory, credentials: credentials, clock: f.Clock);
                }
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public AuthorizationAudit Audit(Guid tenant, Guid actor, Guid id) => new(Ids.NewId(), tenant, actor, "credentials.update", id.ToString("D"), "allowed", "test", DateTimeOffset.UtcNow);
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
