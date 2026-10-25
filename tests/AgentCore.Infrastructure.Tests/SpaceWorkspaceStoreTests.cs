using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using TenantFixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class SpaceWorkspaceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Path_reads_observe_shared_bytes_and_reopen_without_file_rows_or_prior_read_tokens(bool sqlite)
    {
        using var fixture = await Fixture.Create(sqlite);
        var store = fixture.Store(); byte[] bytes = [0, 255, 128, 13, 10];
        const string path = "/home/project/raw.bin";
        await store.WriteTenantAsync(fixture.Owner, path, bytes, Gate);
        Assert.Equal(bytes, (await store.InspectTenantAsync(fixture.Owner, path, (int)AgentWorkspaceLimits.MaxFileBytes)).Bytes);
        var physical = Path.Combine(AgentWorkspacePhysicalPaths.SpaceHomeDirectory(fixture.Workspaces, fixture.Owner.TenantId, fixture.Owner.SpaceId), "project", "raw.bin");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(physical));
        await File.WriteAllTextAsync(physical, "external process");
        Assert.Equal("external process", Encoding.UTF8.GetString((await fixture.Store().InspectTenantAsync(fixture.Owner, path, (int)AgentWorkspaceLimits.MaxFileBytes)).Bytes));
        await store.WriteTenantAsync(fixture.Owner, path, "new writer"u8.ToArray(), Gate);
        Assert.Equal("new writer", await File.ReadAllTextAsync(physical));
        Assert.Empty((await store.ListTenantAsync(fixture.Other, "/home", null, 10)).Items);
        await Assert.ThrowsAsync<AgentCoreException>(() => store.InspectTenantAsync(fixture.Other, path, (int)AgentWorkspaceLimits.MaxFileBytes).AsTask());
        Assert.Empty((await store.InspectTenantAsync(fixture.Owner, path, 0)).Bytes);
        Assert.Equal("new writer"u8.ToArray(), (await store.InspectTenantAsync(fixture.Owner, path, 32)).Bytes);
        if (fixture.Contexts is not null)
        {
            await using var db = fixture.Contexts.CreateDbContext();
            await db.Database.OpenConnectionAsync(); using var query = db.Database.GetDbConnection().CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AgentWorkspaceItems'";
            Assert.Equal(0L, await query.ExecuteScalarAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_repository_names_deep_paths_empty_directories_and_concurrent_writers_survive(bool sqlite)
    {
        using var fixture = await Fixture.Create(sqlite); var store = fixture.Store();
        var deep = "/home/" + string.Join('/', Enumerable.Repeat("deep", 12));
        var made = await store.StructureTenantAsync(fixture.Owner, [new("mkdir", Path: deep + "/empty")], Gate); Assert.True(made.Completed);
        foreach (var name in new[] { ".git/config", ".env", "appsettings.json", "runtime/data", Guid.NewGuid().ToString("D") })
            await store.WriteTenantAsync(fixture.Owner, "/home/" + name, "normal"u8.ToArray(), Gate);
        var path = "/home/concurrent";
        await Task.WhenAll(store.WriteTenantAsync(fixture.Owner, path, "A"u8.ToArray(), Gate).AsTask(), store.WriteTenantAsync(fixture.Owner, path, "B"u8.ToArray(), Gate).AsTask());
        Assert.Contains(Encoding.UTF8.GetString((await fixture.Store().InspectTenantAsync(fixture.Owner, path, (int)AgentWorkspaceLimits.MaxFileBytes)).Bytes), new[] { "A", "B" });
        var page = await fixture.Store().ListTenantAsync(fixture.Owner, deep, null, 10);
        Assert.Contains(page.Items, i => i.LogicalPath == deep + "/empty" && i.Directory);
        Assert.Equal("sampled", page.UsageEnforcement);
    }

    [Fact]
    public async Task Owner_validation_and_current_policy_protect_roots_and_bounded_mutations()
    {
        using var fixture = await Fixture.Create(false); var store = fixture.Store(8, 12);
        await store.WriteTenantAsync(fixture.Owner, "/home/file", "12345678"u8.ToArray(), Gate);
        await Assert.ThrowsAsync<AgentCoreException>(() => store.WriteTenantAsync(fixture.Owner, "/home/too-big", new byte[9], Gate).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => store.WriteTenantAsync(fixture.Owner, "/home/aggregate", new byte[5], Gate).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.WriteTenantAsync(new TenantWorkspaceReference.SpaceHome(fixture.Owner.TenantId, Guid.NewGuid()), "/home/file", "x"u8.ToArray(), Gate).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => store.WriteTenantAsync(fixture.Owner, "/home/../foreign", "x"u8.ToArray(), Gate).AsTask());
        await fixture.Archive();
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.WriteTenantAsync(fixture.Owner, "/home/file", "x"u8.ToArray(), Gate).AsTask());
        Assert.Equal("12345678"u8.ToArray(), (await store.InspectTenantAsync(fixture.Owner, "/home/file", 8)).Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_home_has_independent_files_and_human_Space_authority_while_private_alias_shares_one_quota(bool sqlite)
    {
        using var f = await Fixture.Create(sqlite); var backing = f.Backing; var human = f.Human; var tenant = f.Owner.TenantId;
        async Task<long> Revision() => (await backing.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var spaces = (ITenantSpaceStore)backing.Iam;
        var directory = new TenantSpaceService(backing.Iam, spaces, (ITenantAgentStore)backing.Iam, backing.Authorizer, backing.RunConfiguration(),
            (ITenantCredentialBindingStore)backing.Iam, (ITenantCredentialStore)backing.Iam, backing.Ids, backing.Clock);
        var shared = await directory.CreateAsync(human.PrincipalId, tenant, backing.Ids.NewId(), "Shared room", "", "", await Revision());
        Assert.Empty(await spaces.ReadMembershipsAsync(tenant, shared.SpaceId));
        var files = f.Store(maxRoot: 12);
        await files.WriteTenantAsync(f.Owner, "/home/private.txt", "PRIVATE8"u8.ToArray(), Gate);
        var alias = new TenantWorkspaceReference.SpaceHome(tenant, f.Owner.SpaceId, true);
        Assert.Equal("PRIVATE8"u8.ToArray(), (await files.InspectTenantAsync(alias, "/private/private.txt", 100)).Bytes);
        Assert.Equal(TenantWorkspaceRules.Scope(f.Owner), TenantWorkspaceRules.Scope(alias));
        await Assert.ThrowsAsync<AgentCoreException>(() => files.WriteTenantAsync(alias, "/private/extra.txt", "12345"u8.ToArray(), Gate).AsTask());
        Assert.Equal(8, (await files.ListTenantAsync(alias, "/private", null, 10)).UsedBytes);
        Assert.Equal(8, (await files.ListTenantAsync(f.Owner, "/home", null, 10)).UsedBytes);
        var mutations = new TenantWorkspaceMutationService(backing.Iam, (ITenantAgentStore)backing.Iam, (ITenantSessionStore)backing.Iam,
            files, (ITenantAuthorizationAuditStore)backing.Iam, backing.Identity, backing.Authorizer, backing.Ids, backing.Clock);
        var access = await mutations.InspectAsync(tenant, shared.SpaceId, false, human);
        Assert.True(access.CanWrite); Assert.Equal(shared.Revision, access.OwnerRevision); Assert.Equal(shared.Name, access.WorkspaceName);
        await mutations.WriteAsync(tenant, shared.SpaceId, false, human, await Revision(), "/home/private.txt", "SHARED08"u8.ToArray());
        Assert.Equal("home:" + shared.SpaceId.ToString("D"), Assert.Single(await backing.Iam.ReadAuditAsync(tenant, 200), a => a.ReasonCode == "human_workspace_authorized").Target);
        Assert.Equal("PRIVATE8"u8.ToArray(), (await files.InspectTenantAsync(alias, "/private/private.txt", 100)).Bytes);
        var account = await backing.Identity.RegisterAsync("space-reader@example.test", "Reader", "strong-reader-password");
        var role = await backing.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Workspace read only", ["workspaces.read"], await Revision());
        var invitation = await backing.Access.InviteAsync(human.PrincipalId, tenant, "space-reader@example.test", role.RoleId, await Revision());
        var user = await backing.Access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        var login = (await backing.Identity.SignInAsync("space-reader@example.test", "strong-reader-password"))!;
        var reader = new TenantSessionInitiator(account.AccountId, login.Session.SessionId, user.PrincipalId);
        var reads = new TenantWorkspaceReadService(backing.Iam, (ITenantAgentStore)backing.Iam, (ITenantSessionStore)backing.Iam, files, backing.Identity, backing.Authorizer);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadAsync(tenant, shared.SpaceId, false, reader, "/home/private.txt").AsTask());
        var parent = await backing.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Read this Space", ["spaces.read"], await Revision());
        var grant = await backing.Access.AssignRoleAsync(human.PrincipalId, tenant, reader.PrincipalId, parent.RoleId, TenantSpaceService.Scope(tenant, shared.SpaceId), await Revision());
        Assert.Equal("SHARED08"u8.ToArray(), (await reads.ReadAsync(tenant, shared.SpaceId, false, reader, "/home/private.txt")).Bytes);
        await backing.Access.RevokeAssignmentAsync(human.PrincipalId, tenant, grant.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadAsync(tenant, shared.SpaceId, false, reader, "/home/private.txt").AsTask());
        Assert.Null(await spaces.FindSpaceAsync(Guid.NewGuid(), shared.SpaceId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleted_Shared_Space_releases_protected_references_and_cleans_only_its_owned_files(bool sqlite)
    {
        using var f = await Fixture.Create(sqlite); var backing = f.Backing; var human = f.Human; var tenant = f.Owner.TenantId;
        async Task<long> Revision() => (await backing.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var spaces = (ITenantSpaceStore)backing.Iam; var envStore = (ITenantEnvironmentStore)backing.Iam;
        var directory = new TenantSpaceService(backing.Iam, spaces, (ITenantAgentStore)backing.Iam, backing.Authorizer, backing.RunConfiguration(),
            (ITenantCredentialBindingStore)backing.Iam, (ITenantCredentialStore)backing.Iam, backing.Ids, backing.Clock);
        var deleted = await directory.CreateAsync(human.PrincipalId, tenant, backing.Ids.NewId(), "Delete room", "", "", await Revision());
        var retained = await directory.CreateAsync(human.PrincipalId, tenant, backing.Ids.NewId(), "Retain room", "", "", await Revision());
        var owner = new TenantWorkspaceReference.SpaceHome(tenant, deleted.SpaceId);
        var other = new TenantWorkspaceReference.SpaceHome(tenant, retained.SpaceId); var files = f.Store();
        await files.WriteTenantAsync(owner, "/home/file.txt", "deleted"u8.ToArray(), Gate);
        await files.WriteTenantAsync(other, "/home/file.txt", "retained"u8.ToArray(), Gate);
        await Assert.ThrowsAsync<SecurityConflictException>(() => files.PurgeDeletedSpaceAsync(owner).AsTask());
        var secrets = new TenantSecretService(backing.Iam, (ITenantSecretStore)backing.Iam, backing.Authorizer,
            new AgentCore.Infrastructure.Credentials.LocalCredentialProtector(Path.Combine(backing.Root, "cleanup-keys")), backing.Ids, backing.Clock, backing.Identity);
        var secret = await secrets.CreateAsync(human.PrincipalId, tenant, "Deletion reference", "", "protected-value", await Revision());
        var envs = new TenantEnvironmentService(backing.Iam, spaces, envStore, (ITenantSecretStore)backing.Iam, (ITenantCredentialStore)backing.Iam,
            backing.Authorizer, backing.Identity, backing.Ids, backing.Clock);
        var profile = await envs.SaveAsync(human.PrincipalId, tenant, deleted.SpaceId, backing.Ids.NewId(), "Profile", "", await Revision(), deleted.Revision, 0);
        await envs.SaveVariableAsync(human.PrincipalId, tenant, deleted.SpaceId, profile.EnvironmentId, "TOKEN", AgentCore.Domain.Spaces.EnvironmentVariableKind.SecretReference,
            "", null, secret.SecretId, null, await Revision(), deleted.Revision, profile.Revision, 0);
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await secrets.DeleteAsync(human.PrincipalId, tenant, secret.SecretId, await Revision(), secret.Revision));
        await directory.DeleteAsync(human.PrincipalId, tenant, deleted.SpaceId, await Revision(), deleted.Revision);
        Assert.Null(await envStore.FindEnvironmentAsync(tenant, deleted.SpaceId, profile.EnvironmentId));
        Assert.Empty(await envStore.ReadVariablesAsync(tenant, deleted.SpaceId, profile.EnvironmentId));
        await secrets.DeleteAsync(human.PrincipalId, tenant, secret.SecretId, await Revision(), secret.Revision);
        var page = await spaces.ReadDeletedSpaceWorkspacePageAsync(null, 1);
        Assert.Equal(owner, Assert.Single(page.Items)); Assert.Null(page.NextCursor);
        Assert.True(await spaces.CanPurgeDeletedSpaceWorkspaceAsync(owner));
        var cleanup = new AgentCore.Application.Workspaces.TenantSpaceWorkspaceCleanupService(spaces, files);
        Assert.Equal(1, await cleanup.RecoverAsync()); Assert.Equal(1, await cleanup.RecoverAsync());
        Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.SpaceHomeDirectory(f.Workspaces, tenant, deleted.SpaceId)));
        Assert.Equal("retained"u8.ToArray(), (await f.Store().InspectTenantAsync(other, "/home/file.txt", 100)).Bytes);
        await Assert.ThrowsAsync<AccessDeniedException>(() => files.WriteTenantAsync(owner, "/home/recreate.txt", "blocked"u8.ToArray(), Gate).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Space_cleanup_waits_for_Core_mutation_to_recheck_deletion_and_preserves_the_other_root(bool sqlite)
    {
        using var f = await Fixture.Create(sqlite); var backing = f.Backing; var tenant = f.Owner.TenantId; var actor = f.Human.PrincipalId;
        async Task<long> Revision() => (await backing.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var spaces = (ITenantSpaceStore)backing.Iam;
        var directory = new TenantSpaceService(backing.Iam, spaces, (ITenantAgentStore)backing.Iam, backing.Authorizer, backing.RunConfiguration(),
            (ITenantCredentialBindingStore)backing.Iam, (ITenantCredentialStore)backing.Iam, backing.Ids, backing.Clock);
        var shared = await directory.CreateAsync(actor, tenant, backing.Ids.NewId(), "Deletion race", "", "", await Revision());
        var owner = new TenantWorkspaceReference.SpaceHome(tenant, shared.SpaceId); var files = f.Store();
        await files.WriteTenantAsync(owner, "/home/file.txt", "original"u8.ToArray(), Gate);
        await files.WriteTenantAsync(f.Owner, "/home/retained.txt", "retained"u8.ToArray(), Gate);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask Current(CancellationToken ct)
        {
            entered.TrySetResult(); await release.Task.WaitAsync(ct);
            if (await spaces.FindSpaceAsync(tenant, shared.SpaceId, ct) is null) throw new AccessDeniedException("space_unavailable");
        }
        var write = files.WriteTenantAsync(owner, "/home/file.txt", "racing write"u8.ToArray(), Current).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task? purge = null;
        try
        {
            await directory.DeleteAsync(actor, tenant, shared.SpaceId, await Revision(), shared.Revision);
            purge = f.Store().PurgeDeletedSpaceAsync(owner).AsTask();
            Assert.False(purge.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<AccessDeniedException>(() => write.WaitAsync(TimeSpan.FromSeconds(10)));
        await purge!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.SpaceHomeDirectory(f.Workspaces, tenant, shared.SpaceId)));
        Assert.Equal("retained"u8.ToArray(), (await files.InspectTenantAsync(f.Owner, "/home/retained.txt", 100)).Bytes);
    }

    private static ValueTask Gate(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    private sealed class Fixture : IDisposable
    {
        private TenantFixture backing = null!;
        internal TenantFixture Backing => backing;
        internal TenantSessionInitiator Human => human;
        internal string Workspaces => Path.Combine(backing.Root, "workspaces");
        internal TenantWorkspaceReference.SpaceHome Owner = null!, Other = null!;
        internal Guid InstanceId;
        private TenantSessionInitiator human = null!;
        internal IAgentInstanceStore Instances => backing.Instances;
        internal Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCoreDbContext>? Contexts => backing.Contexts;
        internal FileSpaceWorkspaceStore Store(long maxFile = AgentWorkspaceLimits.MaxFileBytes, long maxRoot = AgentWorkspaceLimits.MaxSpaceBytes) =>
            new(Workspaces, (ITenantSpaceStore)backing.Iam, (ITenantAgentStore)backing.Iam, (ITenantSessionStore)backing.Iam, (ITenantAgentDecommissionStore)backing.Iam, backing.Sandboxes, maxFile, maxRoot);
        internal static async Task<Fixture> Create(bool sqlite)
        {
            var f = new Fixture { backing = await TenantFixture.Create(sqlite, builtInAuthority: true) };
            var first = await f.backing.Identity.BootstrapAsync("files@example.test", "Files", "strong-files-password", "Files", "files");
            var login = (await f.backing.Identity.SignInAsync("files@example.test", "strong-files-password"))!;
            var human = new TenantSessionInitiator(first.AccountId, login.Session.SessionId, first.OwnerPrincipalId);
            f.human = human;
            var homes = new List<TenantWorkspaceReference.SpaceHome>();
            for (var i = 0; i < 2; i++)
            {
                var agent = await f.backing.Agents.CreateAsync(human.PrincipalId, first.TenantId, "examiner", 1,
                    (await f.backing.Iam.ReadTenantAsync(first.TenantId))!.Tenant.Revision, initiator: human);
                var space = (await ((ITenantSpaceStore)f.backing.Iam).FindPrivateSpaceAsync(first.TenantId, agent.Instance.InstanceId))!;
                homes.Add(new(first.TenantId, space.SpaceId)); if (i == 0) f.InstanceId = agent.Instance.InstanceId;
            }
            f.Owner = homes[0]; f.Other = homes[1]; return f;
        }
        internal async Task Archive() => await backing.Agents.SetLifecycleAsync(human.PrincipalId, Owner.TenantId, InstanceId, AgentInstanceLifecycle.Archived,
            (await backing.Iam.ReadTenantAsync(Owner.TenantId))!.Tenant.Revision, 1, initiator: human);
        public void Dispose() => backing.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
