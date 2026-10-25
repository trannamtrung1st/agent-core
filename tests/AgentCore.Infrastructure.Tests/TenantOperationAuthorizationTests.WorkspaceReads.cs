using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Workspaces;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_reads_use_actual_parent_and_independent_home_working_scopes_without_setup(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var store = WorkspaceReader(f); var service = WorkspaceService(f, store);
        var home = AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), setup.Tenant, SpaceId(setup));
        var working = AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), setup.Agent.Instance.InstanceId, setup.Run.SessionId);
        Assert.Empty((await service.ListAsync(setup.Tenant, SpaceId(setup), false, setup.Owner)).Items);
        Assert.Empty((await service.ListAsync(setup.Tenant, setup.Run.SessionId, true, setup.Owner)).Items);
        Assert.False(Directory.Exists(home)); Assert.False(Directory.Exists(working));
        var source = new CanonicalWorkspaceFilesystem(home, "/home"); source.Ensure();
        await source.WriteAsync("/home/private.txt", Encoding.UTF8.GetBytes("Private home thử nghiệm"), 1024, 4096);
        var scratch = new CanonicalWorkspaceFilesystem(working, "/working"); scratch.Ensure();
        await scratch.WriteAsync("/working/private.txt", "Private scratch"u8.ToArray(), 1024, 4096);
        var reader = await User(f, setup, "workspace-reader@example.test", ["agents.read", "sessions.read"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, SpaceId(setup), false, reader).AsTask());
        var homeScope = TenantWorkspaceRules.Home(setup.Tenant, SpaceId(setup)); var scratchScope = TenantWorkspaceRules.Working(setup.Tenant, setup.Run.SessionId);
        Assert.True(await f.Iam.OwnsResourceAsync(homeScope)); Assert.True(await f.Iam.OwnsResourceAsync(scratchScope));
        Assert.False(await f.Iam.OwnsResourceAsync(homeScope with { TenantId = f.Ids.NewId() }));
        Assert.False(await f.Iam.OwnsResourceAsync(TenantWorkspaceRules.Home(setup.Tenant, setup.Run.SessionId)));
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read exact workspace", ["workspaces.read"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, reader.PrincipalId, role.RoleId, homeScope, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal("/home/private.txt", Assert.Single((await service.ListAsync(setup.Tenant, SpaceId(setup), false, reader)).Items).LogicalPath);
        Assert.Equal("Private home thử nghiệm", Encoding.UTF8.GetString((await service.ReadAsync(setup.Tenant, SpaceId(setup), false, reader, "/home/private.txt")).Bytes));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, setup.Run.SessionId, true, reader, "/working/private.txt").AsTask());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, reader.PrincipalId, role.RoleId, scratchScope, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal("Private scratch", Encoding.UTF8.GetString((await service.ReadAsync(setup.Tenant, setup.Run.SessionId, true, reader, "/working/private.txt")).Bytes));
        Assert.Empty((await service.MetadataAsync(setup.Tenant, setup.Run.SessionId, true, reader, "/working/private.txt")).Bytes);
        Assert.Single((await service.SearchAsync(setup.Tenant, SpaceId(setup), false, reader, "private")).Items);
        await Assert.ThrowsAsync<AgentCoreException>(() => service.ReadAsync(setup.Tenant, SpaceId(setup), false, reader, "/working/private.txt").AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.InspectTenantAsync(new TenantWorkspaceReference.SessionWorking(setup.Tenant, f.Ids.NewId()), "/working/private.txt", 1024).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ListTenantAsync(new TenantWorkspaceReference.SpaceHome(setup.Tenant, f.Ids.NewId()), "/home", null, 10).AsTask());
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, SpaceId(setup), false, reader, "/home/private.txt").AsTask());
        Assert.Equal("Private scratch", Encoding.UTF8.GetString((await service.ReadAsync(setup.Tenant, setup.Run.SessionId, true, reader, "/working/private.txt")).Bytes));
    }

    [Theory]
    [InlineData(false, false, "revoke")] [InlineData(true, false, "revoke")] [InlineData(false, true, "revoke")] [InlineData(true, true, "revoke")]
    [InlineData(false, false, "logout")] [InlineData(true, false, "logout")] [InlineData(false, true, "logout")] [InlineData(true, true, "logout")]
    [InlineData(false, false, "source")] [InlineData(true, false, "source")] [InlineData(false, true, "source")] [InlineData(true, true, "source")]
    [InlineData(false, true, "delete")] [InlineData(true, true, "delete")]
    public async Task Tenant_workspace_reads_withhold_bytes_after_original_access_or_source_changes_during_IO(bool sqlite, bool working, string change)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        var read = await WorkspaceReadCase(f, setup, working); var store = WorkspaceReader(f);
        var hook = new WorkspaceReadHook(store, async () =>
        {
            if (change == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, read.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
            if (change == "logout")
            {
                await f.Identity.SignOutAsync(read.Reader.AuthenticationSessionId);
                Assert.NotEqual(read.Reader.AuthenticationSessionId, (await f.Identity.SignInAsync("workspace-fenced-reader@example.test", "strong-operation-password"))!.Session.SessionId);
            }
            if (change == "source") await read.Files.WriteAsync(read.Path, "Changed private source with another size"u8.ToArray(), 1024, 4096);
            if (change == "delete")
            {
                await CancelArtifactRun(f, setup, frame);
                var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
                await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(frame.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
            }
        });
        var attempt = WorkspaceService(f, hook).ReadAsync(setup.Tenant, working ? setup.Run.SessionId : setup.Agent.Instance.InstanceId, working, read.Reader, read.Path).AsTask();
        if (change == "source") Assert.Equal("workspace_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => attempt)).ReasonCode);
        else await Assert.ThrowsAsync<AccessDeniedException>(() => attempt);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_projection_rechecks_authority_after_processing(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var read = await WorkspaceReadCase(f, setup, working);
        var processed = false;
        await Assert.ThrowsAsync<AccessDeniedException>(() => WorkspaceService(f, WorkspaceReader(f)).ProjectAsync(setup.Tenant,
            working ? setup.Run.SessionId : setup.Agent.Instance.InstanceId, working, read.Reader, read.Path, _ => 1024, async (content, token) =>
            {
                var text = Encoding.UTF8.GetString(content.Bytes); processed = true;
                await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, read.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, token);
                return text;
            }).AsTask());
        Assert.True(processed);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_scope_retirement_is_atomic_and_preserves_home_and_future_IAM_writes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        var read = await WorkspaceReadCase(f, setup, true);
        var home = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, read.Reader.PrincipalId, read.Grant.RoleId,
            TenantWorkspaceRules.Home(setup.Tenant, SpaceId(setup)), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await CancelArtifactRun(f, setup, frame);
        var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        await Assert.ThrowsAsync<ArgumentException>(() => ((ITenantSessionStore)f.Iam).TryChangeTenantSessionAsync(
            state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            new(frame.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), setup.Owner, f.Clock.GetUtcNow(),
            new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "sessions.delete", frame.SessionId.ToString("D"), "allowed", "test", f.Clock.GetUtcNow())).AsTask());
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(frame.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), state.Tenant.Revision);
        state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        Assert.DoesNotContain(state.Assignments, a => a.AssignmentId == read.Grant.AssignmentId); Assert.Contains(state.Assignments, a => a.AssignmentId == home.AssignmentId);
        Assert.False(await f.Iam.OwnsResourceAsync(read.Grant.Scope)); Assert.True(await f.Iam.OwnsResourceAsync(home.Scope));
        await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "After workspace retirement", ["workspaces.read"], state.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, read.Reader.PrincipalId, read.Grant.RoleId, read.Grant.Scope, (f.Iam.ReadTenantAsync(setup.Tenant).Result)!.Tenant.Revision).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_owner_addresses_reject_foreign_same_Account_resources_and_unowned_backing_scope_injection(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var read = await WorkspaceReadCase(f, setup, true);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == setup.Owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, setup.Owner.AccountId, "Beta", "beta");
        var betaActor = setup.Owner with { PrincipalId = beta.Users.Single().PrincipalId };
        var betaAgent = await f.Agents.CreateAsync(betaActor.PrincipalId, beta.Tenant.TenantId, "examiner", 1, beta.Tenant.Revision);
        var betaScope = TenantWorkspaceRules.Home(beta.Tenant.TenantId, betaAgent.Instance.InstanceId);
        var forged = betaScope with { TenantId = setup.Tenant };
        Assert.True(await f.Iam.OwnsResourceAsync(betaScope)); Assert.False(await f.Iam.OwnsResourceAsync(forged));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, read.Reader.PrincipalId, read.Grant.RoleId, forged, (f.Iam.ReadTenantAsync(setup.Tenant).Result)!.Tenant.Revision).AsTask());
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var injection = new RoleAssignment(f.Ids.NewId(), setup.Tenant, read.Reader.PrincipalId, read.Grant.RoleId, forged, setup.Owner.PrincipalId, f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Iam.TrySaveTenantAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 }, Assignments = state.Assignments.Append(injection).ToArray() },
            state.Tenant.Revision, new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "iam.roles.assign", "test", "allowed", "test", f.Clock.GetUtcNow())).AsTask());
        var service = WorkspaceService(f, WorkspaceReader(f));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, betaAgent.Instance.InstanceId, false, setup.Owner).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(beta.Tenant.TenantId, betaAgent.Instance.InstanceId, false, setup.Owner).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(beta.Tenant.TenantId, betaAgent.Instance.InstanceId, false, read.Reader).AsTask());
        Assert.Empty((await service.ListAsync(beta.Tenant.TenantId, betaAgent.Instance.InstanceId, false, betaActor)).Items);
        var sibling = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, setup.Agent.Instance.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, sibling.Owner.SessionId, true, read.Reader).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => WorkspaceReader(f).ListTenantAsync(new TenantWorkspaceReference.SessionWorking(beta.Tenant.TenantId, setup.Run.SessionId), "/working", null, 10).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_safe_links_remain_readable_and_outside_targets_are_not_disclosed_or_followed(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var read = await WorkspaceReadCase(f, setup, false);
        var root = AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), setup.Tenant, SpaceId(setup));
        File.CreateSymbolicLink(Path.Combine(root, "inside.txt"), "private.txt");
        var external = Path.Combine(f.Root, "outside-private.txt"); await File.WriteAllTextAsync(external, "Do not disclose");
        File.CreateSymbolicLink(Path.Combine(root, "outside.txt"), external);
        var service = WorkspaceService(f, WorkspaceReader(f));
        Assert.Equal("Current private content", Encoding.UTF8.GetString((await service.ReadAsync(setup.Tenant, SpaceId(setup), false, read.Reader, "/home/inside.txt")).Bytes));
        var page = await service.ListAsync(setup.Tenant, SpaceId(setup), false, read.Reader);
        Assert.Equal("private.txt", page.Items.Single(i => i.LogicalPath == "/home/inside.txt").LinkTarget); Assert.Null(page.Items.Single(i => i.LogicalPath == "/home/outside.txt").LinkTarget);
        Assert.Null(Assert.Single((await service.SearchAsync(setup.Tenant, SpaceId(setup), false, read.Reader, "outside")).Items).LinkTarget);
        await Assert.ThrowsAsync<AgentCoreException>(() => service.ReadAsync(setup.Tenant, SpaceId(setup), false, read.Reader, "/home/outside.txt").AsTask());
        Assert.DoesNotContain(f.Root, System.Text.Json.JsonSerializer.Serialize(page));
        Assert.Equal("Do not disclose", await File.ReadAllTextAsync(external));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_read_does_not_replace_independent_parent_read(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var reader = await User(f, setup, "workspace-without-parent@example.test", ["workspaces.read"]);
        var service = WorkspaceService(f, WorkspaceReader(f));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, SpaceId(setup), false, reader).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, setup.Run.SessionId, true, reader).AsTask());
        Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(f.Root, "workspaces"), setup.Agent.Instance.InstanceId)));
    }

    private static async Task<(TenantSessionInitiator Reader, RoleAssignment Grant, CanonicalWorkspaceFilesystem Files, string Path)> WorkspaceReadCase(Fixture f, Environment setup, bool working)
    {
        var root = Path.Combine(f.Root, "workspaces"); _ = WorkspaceReader(f);
        var path = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(root, setup.Agent.Instance.InstanceId, setup.Run.SessionId)
            : AgentWorkspacePhysicalPaths.SpaceHomeDirectory(root, setup.Tenant, SpaceId(setup));
        var files = new CanonicalWorkspaceFilesystem(path, working ? "/working" : "/home"); files.Ensure();
        var file = (working ? "/working" : "/home") + "/private.txt"; await files.WriteAsync(file, "Current private content"u8.ToArray(), 1024, 4096);
        var reader = await User(f, setup, "workspace-fenced-reader@example.test", ["agents.read", "sessions.read"]);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Exact workspace read", ["workspaces.read"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, reader.PrincipalId, role.RoleId,
            working ? TenantWorkspaceRules.Working(setup.Tenant, setup.Run.SessionId) : TenantWorkspaceRules.Home(setup.Tenant, SpaceId(setup)), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        return (reader, grant, files, file);
    }
    private sealed class WorkspaceReadHook(ITenantWorkspaceReadStore inner, Func<Task> afterRead) : ITenantWorkspaceReadStore
    {
        private int _fired;
        public ValueTask<AgentWorkspacePage> ListTenantAsync(TenantWorkspaceReference owner, string path, string? cursor, int limit, CancellationToken ct = default) => inner.ListTenantAsync(owner, path, cursor, limit, ct);
        public ValueTask<AgentWorkspacePage> SearchTenantAsync(TenantWorkspaceReference owner, string query, string? cursor, int limit, CancellationToken ct = default) => inner.SearchTenantAsync(owner, query, cursor, limit, ct);
        public async ValueTask<AgentWorkspaceContent> InspectTenantAsync(TenantWorkspaceReference owner, string path, int maxContentBytes, CancellationToken ct = default)
        { var result = await inner.InspectTenantAsync(owner, path, maxContentBytes, ct); if (maxContentBytes > 0 && Interlocked.Exchange(ref _fired, 1) == 0) await afterRead(); return result; }
    }
    private static Guid SpaceId(Environment value) => TenantWorkspaceRules.Context(value.Run).SpaceId;
    private static TenantWorkspaceReference WorkspaceOwner(Environment value, bool working = false) =>
        TenantWorkspaceRules.ForRun(value.Run, working ? "/working" : "/home");
    private static FileSpaceWorkspaceStore WorkspaceReader(Fixture f) => new(Path.Combine(f.Root, "workspaces"), (ITenantSpaceStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentDecommissionStore)f.Iam, f.Sandboxes);
    private static TenantWorkspaceReadService WorkspaceService(Fixture f, ITenantWorkspaceReadStore store) => new(f.Iam, (ITenantAgentStore)f.Iam,
        (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer);
}
