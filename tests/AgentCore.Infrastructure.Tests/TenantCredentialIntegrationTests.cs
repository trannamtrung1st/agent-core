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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
    internal sealed partial class Fixture(TimeProvider? clock = null) : IAsyncDisposable
    {
        public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tenant-credentials-" + Guid.NewGuid().ToString("N"));
        public IIamStore Iam { get; private set; } = null!;
        public ITenantCredentialStore Credentials => (ITenantCredentialStore)Iam;
        public IArtifactStore Artifacts { get; private set; } = null!;
        public IAttachmentStore Attachments { get; private set; } = null!;
        public IEmailStore EmailDrafts { get; private set; } = null!;
        public ISandboxStore Sandboxes { get; private set; } = null!;
        public ICredentialStore Legacy { get; private set; } = null!;
        public IAgentInstanceStore Instances { get; private set; } = null!;
        public IMemoryStore Memory { get; private set; } = null!;
        public TenantSessionService Sessions => SessionService((ITenantSessionStore)Iam);
        public TenantChatService ChatService(ITenantAgentStore? agents = null) => new(Iam, agents ?? (ITenantAgentStore)Iam,
            (ITenantDefinitionStore)Iam, BuiltIns, Identity, Authorizer, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(Iam));
        public TenantSessionService SessionService(ITenantSessionStore sessions) => new(Iam, (ITenantAgentStore)Iam, sessions, (ITenantRunStore)Iam, (ITenantDefinitionStore)Iam, BuiltIns,
            Identity, Authorizer, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(Iam), new(), Ids, Clock, Artifacts as ITenantArtifactStore, Attachments as ITenantAttachmentStore);
        public IAgentDefinitionResourceAdminStore Resources { get; private set; } = null!;
        public IDefinitionResourceContentStore RunContent { get; } = new InMemoryDefinitionResourceContentStore();
        public TenantRunService RunService(ITenantRunStore? runs = null, ITenantSessionInputControl? inputs = null) => new(Iam, (ITenantSessionStore)Iam, runs ?? (ITenantRunStore)Iam,
            RunConfiguration(),
            Identity, Authorizer, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(Iam), Ids, Clock, attachments: Attachments as ITenantAttachmentStore, inputs: inputs);
        public TenantRunConfigurationResolver RunConfiguration(IBuiltInAgentDefinitionStore? currentBuiltIns = null) =>
            new((ITenantAgentStore)Iam, (ITenantDefinitionStore)Iam, currentBuiltIns ?? BuiltIns, RunResourceCatalog(), (ITenantCredentialBindingStore)Iam);
        public TenantAgentConfigurationService AgentConfiguration(ITenantModelCatalogReader? modelCatalogs = null) => new(Iam, (ITenantAgentStore)Iam, (ITenantDefinitionStore)Iam,
            BuiltIns, RunResourceCatalog(), Authorizer, modelCatalogs ?? new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(Iam), AgentCore.Application.Tools.ToolConfigurationGates.AllowAll,
            new(), Ids, Clock, content: RunContent, identity: Identity);
        public IIdGenerator Ids { get; } = new SystemIdGenerator(TimeProvider.System);
        public IIdentityTokens Tokens { get; } = new IdentityTokens();
        public IamAuthorizer Authorizer => new(Iam);
        public IdentityService Identity => new(Iam, new AccountPasswordHasher(), Tokens, Ids, Clock);
        public TenantAccessService Access => new(Iam, Authorizer, Identity, Tokens, Ids, Clock);
        public LocalCredentialProtector Protector => new(Path.Combine(Root, "keys"));
        public TenantCredentialService Service => new(Iam, Credentials, Authorizer, Protector, Ids, Clock, Identity);
        public TenantCredentialBindingService BindingService => BindingServiceFor((ITenantCredentialBindingStore)Iam);
        public TenantCredentialBindingService BindingServiceFor(ITenantCredentialBindingStore bindings) => new(Iam, (ITenantAgentStore)Iam, Credentials, bindings, Authorizer, Identity,
            BuiltIns, (ITenantDefinitionStore)Iam, Ids, Clock);
        public TenantAgentService Agents => new(Iam, (ITenantAgentStore)Iam, BuiltIns, Authorizer, Ids, Clock, ownedDefinitions: (ITenantDefinitionStore)Iam);
        private IBuiltInAgentDefinitionStore? _builtIns;
        private AgentCore.Application.Agents.InheritedDefinitionResourceCatalog RunResourceCatalog()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
            var root = Path.Combine(directory!.FullName, "agents");
            return new(Resources,
                BuiltIns, new FileApprovedKnowledgeCatalog(root), RunContent);
        }
        internal IBuiltInAgentDefinitionStore BuiltIns => _builtIns ??= LoadBuiltIns();
        private static IBuiltInAgentDefinitionStore LoadBuiltIns()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
            return new FileAgentDefinitionStore(Path.Combine(directory!.FullName, "agents"), SyntheticProviderAliases.Default);
        }
        public IDbContextFactory<AgentCoreDbContext>? Contexts { get; private set; }
        public static async Task<Fixture> Create(bool sqlite, TimeProvider? clock = null, bool builtInAuthority = false, bool artifactAuthority = false, bool attachmentAuthority = false, string? targetMigration = null)
        {
            if (!sqlite && targetMigration is not null) throw new ArgumentException("A historical schema requires SQLite.", nameof(targetMigration));
            var f = new Fixture(clock); Directory.CreateDirectory(f.Root);
            try
            {
                if (sqlite)
                {
                    if (targetMigration is null) await CurrentSqliteTestDatabase.SeedAsync(Path.Combine(f.Root, "store.db"));
                    f.Contexts = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={f.Root}/store.db;Foreign Keys=True;Default Timeout=10").AddInterceptors(new TaskDatabaseFailureCapture(f.DatabaseErrors)).Options);
                    await using (var db = f.Contexts.CreateDbContext()) { await db.GetService<IMigrator>().MigrateAsync(targetMigration); Assert.False(db.Database.HasPendingModelChanges()); }
                    if (attachmentAuthority) f.Attachments = new SqliteAttachmentStore(f.Contexts, f.Clock, Path.Combine(f.Root, "attachments"));
                    if (artifactAuthority) f.Artifacts = new SqliteArtifactStore(f.Contexts, f.Clock, Path.Combine(f.Root, "artifacts"));
                    f.Sandboxes = new SqliteSandboxStore(f.Contexts);
                    f.EmailDrafts = new SqliteEmailStore(f.Contexts);
                    f.Resources = new SqliteAgentDefinitionResourceAdminStore(f.Contexts, f.RunContent, f.Ids);
                    f.Iam = new SqliteIamStore(f.Contexts, definitionContent: f.RunContent, builtIns: builtInAuthority ? f.BuiltIns : null, inheritedResources: builtInAuthority ? f.RunResourceCatalog() : null, clock: f.Clock, artifactBacking: artifactAuthority ? (SqliteArtifactStore)f.Artifacts : null, attachmentBacking: attachmentAuthority ? (SqliteAttachmentStore)f.Attachments : null); f.Legacy = new SqliteCredentialStore(f.Contexts); f.Instances = new SqliteAgentInstanceStore(f.Contexts, f.Ids);
                    f.Memory = new SqliteMemoryStore(f.Contexts, f.Clock);
                }
                else
                {
                    var instances = new InMemoryAgentInstanceStore(); var credentials = new InMemoryCredentialStore(instances);
                    f.Legacy = credentials; f.Instances = instances;
                    var memory = new InMemoryMemoryStore(instances); f.Memory = memory;
                    if (attachmentAuthority) f.Attachments = new InMemoryAttachmentStore(f.Clock).BindTenantAuthority(memory);
                    if (artifactAuthority) f.Artifacts = new InMemoryArtifactStore(f.Clock).BindTenantAuthority(memory);
                    f.Sandboxes = new InMemorySandboxStore();
                    f.EmailDrafts = new InMemoryEmailStore();
                    var definitions = new InMemoryAgentDefinitionAdminStore(f.Ids);
                    var resources = new InMemoryAgentDefinitionResourceAdminStore(definitions, f.RunContent, f.Ids); definitions.ResourceStore = resources; f.Resources = resources;
                    f.Iam = new InMemoryIamStore(instances, definitions, memory, builtIns: builtInAuthority ? f.BuiltIns : null, inheritedResources: builtInAuthority ? f.RunResourceCatalog() : null, credentials: credentials, clock: f.Clock, sandboxProfiles: (InMemorySandboxStore)f.Sandboxes, artifacts: artifactAuthority ? (InMemoryArtifactStore)f.Artifacts : null, attachments: attachmentAuthority ? (InMemoryAttachmentStore)f.Attachments : null, emailDrafts: (InMemoryEmailStore)f.EmailDrafts);
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
