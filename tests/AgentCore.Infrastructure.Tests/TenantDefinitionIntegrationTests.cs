using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_definitions_publish_immutable_versions_and_reject_foreign_scopes_and_global_paths(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Private Alpha", 1);
        var id = Guid.Parse(draft.DefinitionId);
        Assert.Null(await f.Admin.GetDraftAsync(draft.DraftId));
        Assert.Empty(await f.Admin.ListDraftsAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Admin.CreateDraftAsync(new(draft.DefinitionId, draft.Candidate,
            DefinitionDraftSourceKind.New, null, DateTimeOffset.UtcNow)).AsTask());
        var alias = Guid.Parse(draft.DefinitionId).ToString("N");
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Admin.CreateDraftAsync(new(alias, draft.Candidate with { DefinitionId = alias },
            DefinitionDraftSourceKind.New, null, DateTimeOffset.UtcNow)).AsTask());
        Assert.Equal(0, (await f.Service.ListAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.ReadDraftAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId, draft.DraftId).AsTask());
        var p1 = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 2, 1);
        Assert.Equal(1, p1.Version); Assert.Null(await f.Admin.GetPublicationAsync(draft.DefinitionId, 1));
        Assert.Empty(await f.Admin.ListPublicationsAsync());
        var fork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, id, 1, 3);
        var edited = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId,
            fork.Candidate with { AgentInstructions = "Second immutable publication." }, 4, 1);
        var p2 = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, 5, edited.Revision);
        Assert.Equal(2, p2.Version);
        Assert.Equal(draft.Candidate.AgentInstructions, (await f.Definitions.FindDefinitionPublicationAsync(owner.TenantId, id, 1))!.Payload.AgentInstructions);
        var nextFork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, id, 2, 6);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Evaluation.SaveResultAsync(new(nextFork.DraftId, 1, "forged", "test", 1, "test", true, [], DateTimeOffset.UtcNow)).AsTask());
        var state = (await f.Iam.ReadTenantAsync(beta.Tenant.TenantId))!;
        var foreign = new RoleAssignment(f.Ids.NewId(), state.Tenant.TenantId, state.Users.Single().PrincipalId,
            state.Roles.Single(r => r.Key == "reader").RoleId, AccessScope.ForResource(state.Tenant.TenantId, "definition", draft.DefinitionId), state.Users.Single().PrincipalId, DateTimeOffset.UtcNow);
        var decision = await f.Authorizer.CheckAsync(foreign.PrincipalId, "agent-definitions.read", foreign.Scope);
        Assert.False(decision.Allowed); Assert.Equal("resource_unavailable", decision.ReasonCode);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Iam.TrySaveTenantAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 }, Assignments = state.Assignments.Append(foreign).ToArray() },
            state.Tenant.Revision, f.Audit(state.Tenant.TenantId, foreign.PrincipalId, id)).AsTask());
        var alphaState = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var collisionId = f.Ids.NewId(); var collision = nextFork with { DefinitionId = collisionId.ToString("D"),
            Candidate = nextFork.Candidate with { DefinitionId = collisionId.ToString("D") }, SourceKind = DefinitionDraftSourceKind.New, SourceVersion = null };
        Assert.False(await f.Definitions.TryCreateDefinitionAsync(alphaState with { Tenant = alphaState.Tenant with { Revision = alphaState.Tenant.Revision + 1 } },
            alphaState.Tenant.Revision, new(owner.TenantId, collisionId, DateTimeOffset.UtcNow), collision, [], f.Audit(owner.TenantId, owner.OwnerPrincipalId, collisionId)));
        Assert.Null(await f.Definitions.FindDefinitionAsync(owner.TenantId, collisionId));
        Assert.Equal(alphaState.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            Assert.Equal("Second immutable publication.", (await reopened.FindDefinitionPublicationAsync(owner.TenantId, id, 2))!.Payload.AgentInstructions);
            Assert.Equal(nextFork.DraftId, Assert.Single(await reopened.ListDefinitionDraftsAsync(owner.TenantId, id)).DraftId);
            Assert.Null(await reopened.FindDefinitionAsync(beta.Tenant.TenantId, id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Account_suspension_fences_prepared_definition_changes_without_losing_the_draft(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var account = await f.Identity.RegisterAsync("creator@example.test", "Creator", "strong-creator-password");
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var invite = await f.Access.InviteAsync(owner.OwnerPrincipalId, owner.TenantId, account.LoginKey, state.Roles.Single(r => r.Key == "contributor").RoleId, 1);
        var creator = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invite.Invitation.InvitationId, invite.Token);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var draft = await f.Service.CreateAsync(creator.PrincipalId, owner.TenantId, "Creator draft", state.Tenant.Revision);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var edited = draft with { Revision = 2, Candidate = draft.Candidate with { AgentInstructions = "Must not commit." } };
        var admin = new AccountAdministrationService(f.Iam, f.Authorizer, f.Tokens, f.Ids, TimeProvider.System);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
        await admin.SetStatusAsync(system.PrincipalId, account.AccountId, IdentityStatus.Suspended);
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Definitions.TrySaveDefinitionDraftAsync(next, state.Tenant.Revision, edited, 1,
            f.Audit(owner.TenantId, creator.PrincipalId, Guid.Parse(draft.DefinitionId))).AsTask());
        Assert.Equal("account_unavailable", error.ReasonCode);
        Assert.Equal(1, (await f.Definitions.FindDefinitionDraftAsync(owner.TenantId, draft.DraftId))!.Revision);
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public IIamStore Iam { get; private set; } = null!;
        public ITenantDefinitionStore Definitions => (ITenantDefinitionStore)Iam;
        public IAgentDefinitionAdminStore Admin { get; private set; } = null!;
        public IAgentInstanceStore Instances { get; private set; } = null!;
        public IMemoryStore SessionStore { get; private set; } = null!;
        public ISandboxStore Sandbox { get; private set; } = null!;
        public IAgentDefinitionResourceAdminStore Resources { get; private set; } = null!;
        public IDefinitionDraftEvaluationStore Evaluation { get; private set; } = null!;
        public IDefinitionResourceContentStore Content { get; private set; } = new InMemoryDefinitionResourceContentStore();
        public IIdGenerator Ids { get; } = new SystemIdGenerator(TimeProvider.System);
        public IIdentityTokens Tokens { get; } = new IdentityTokens();
        public IamAuthorizer Authorizer => new(Iam);
        public IdentityService Identity => new(Iam, new AccountPasswordHasher(), Tokens, Ids, TimeProvider.System);
        public TenantAccessService Access => new(Iam, Authorizer, Identity, Tokens, Ids, TimeProvider.System);
        public AgentCore.Application.Evaluations.TenantEvaluationService Evaluations(IDefinitionDraftSyntheticBehaviorEvaluator? evaluator = null) => new(Iam, (ITenantEvaluationStore)Iam,
            Definitions, (ITenantRunStore)Iam, (ITenantSessionStore)Iam, Authorizer, Identity, Ids, TimeProvider.System,
            new(evaluator ?? Evaluator, ToolConfigurationGates.AllowAll), Content);
        public IDefinitionDraftSyntheticBehaviorEvaluator Evaluator => new AgentCore.Infrastructure.Admin.SyntheticDefinitionDraftBehaviorEvaluator(new AgentCore.Infrastructure.Admin.DefinitionDraftSyntheticOfflineLanguageModel(), ToolConfigurationGates.AllowAll);
        private IBuiltInDefinitionTemplateSource? _shippedTemplates;
        public TenantDefinitionService Service => WithEvaluator(Evaluator);
        public TenantDefinitionService WithEvaluator(IDefinitionDraftSyntheticBehaviorEvaluator evaluator) => WithTemplates(_shippedTemplates ??= ShippedTemplates(), evaluator);
        public TenantDefinitionService WithTemplates(IBuiltInDefinitionTemplateSource templates, IDefinitionDraftSyntheticBehaviorEvaluator? evaluator = null, IDefinitionResourceContentStore? resourceContent = null) => new(Iam, Definitions, Authorizer, SyntheticProviderAliases.Default, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(Iam), ToolConfigurationGates.AllowAll, Ids, TimeProvider.System, resourceContent ?? Content, templates, Identity);
        public static IBuiltInDefinitionTemplateSource ShippedTemplates()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
            var root = Path.Combine(directory!.FullName, "agents");
            return new FileBuiltInDefinitionTemplateSource(new FileAgentDefinitionStore(root, SyntheticProviderAliases.Default), root, Path.Combine(root, "templates"));
        }
        public IDbContextFactory<AgentCoreDbContext>? Contexts { get; private set; }
        private string? _path;
        public string? DatabasePath => _path;
        public static async Task<Fixture> Create(bool sqlite, string? targetMigration = null)
        {
            var f = new Fixture();
            if (sqlite)
            {
                f._path = Path.Combine(Path.GetTempPath(), $"tenant-definition-{Guid.NewGuid():N}.db");
                if (targetMigration is null) await CurrentSqliteTestDatabase.SeedAsync(f._path);
                f.Content = new FileDefinitionResourceContentStore(f._path + ".resources");
                f.Contexts = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={f._path};Foreign Keys=True;Default Timeout=10").Options);
                await using (var db = f.Contexts.CreateDbContext()) await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db).MigrateAsync(targetMigration);
                f.Iam = new SqliteIamStore(f.Contexts, f.Content); f.Admin = new SqliteAgentDefinitionAdminStore(f.Contexts, f.Ids, f.Content);
                f.Sandbox = new SqliteSandboxStore(f.Contexts);
                f.Instances = new SqliteAgentInstanceStore(f.Contexts, f.Ids); f.SessionStore = new SqliteMemoryStore(f.Contexts, TimeProvider.System);
                f.Resources = new SqliteAgentDefinitionResourceAdminStore(f.Contexts, f.Content, f.Ids); f.Evaluation = new SqliteDefinitionDraftEvaluationStore(f.Contexts, f.Ids);
            }
            else
            {
                var admin = new InMemoryAgentDefinitionAdminStore(f.Ids); var resources = new InMemoryAgentDefinitionResourceAdminStore(admin, f.Content, f.Ids);
                admin.ResourceStore = resources; f.Admin = admin; f.Resources = resources; f.Evaluation = new InMemoryDefinitionDraftEvaluationStore(admin);
                var instances = new InMemoryAgentInstanceStore(); var sessions = new InMemoryMemoryStore();
                var sandbox = new InMemorySandboxStore(); f.Sandbox = sandbox;
                f.Instances = instances; f.SessionStore = sessions; f.Iam = new InMemoryIamStore(instances, admin, sessions, credentials: new InMemoryCredentialStore(instances), sandboxProfiles: sandbox);
            }
            return f;
        }
        public AuthorizationAudit Audit(Guid tenant, Guid actor, Guid target) => new(Ids.NewId(), tenant, actor, "agent-definitions.publish", target.ToString("D"), "allowed", "test", DateTimeOffset.UtcNow);
        public async Task SeedEvidence(AgentDefinitionDraft draft, AgentDefinitionDraftResource resource, DefinitionEvaluationScenario scenario, DefinitionEvaluationResult? result)
        {
            if (Contexts is { } contexts)
            {
                await using var db = contexts.CreateDbContext(); var key = draft.DraftId.ToString("D");
                await db.AgentDefinitionDraftResources.IgnoreQueryFilters().Where(r => r.DraftId == key).ExecuteDeleteAsync();
                db.Add(DefinitionResourcePersistence.MapDraftResource(resource));
                await db.SaveChangesAsync();
            }
            else
            {
                var admin = (InMemoryAgentDefinitionAdminStore)Admin; admin.ResourceStore!.RestoreDraftResources(draft.DraftId, [resource]);
            }
        }
        public ValueTask DisposeAsync()
        { if (_path is not null) { SqliteConnection.ClearAllPools(); File.Delete(_path); File.Delete(_path + "-shm"); File.Delete(_path + "-wal"); if (Directory.Exists(_path + ".resources")) Directory.Delete(_path + ".resources", recursive: true); } return ValueTask.CompletedTask; }
    }
    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
