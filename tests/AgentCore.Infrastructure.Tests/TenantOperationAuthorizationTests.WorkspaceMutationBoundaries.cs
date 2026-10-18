using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Workspaces;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    public static IEnumerable<object[]> WorkspaceMutationRaces()
    {
        foreach (var sqlite in new[] { false, true })
        foreach (var working in new[] { false, true })
        foreach (var delete in new[] { false, true })
        foreach (var change in new[] { "revoke", "logout", "archive", "security" })
            yield return [sqlite, working, delete, change];
        foreach (var sqlite in new[] { false, true })
        foreach (var delete in new[] { false, true }) yield return [sqlite, true, delete, "delete"];
    }

    [Theory] [MemberData(nameof(WorkspaceMutationRaces))]
    public async Task Tenant_workspace_mutations_preserve_destination_when_current_authority_changes_at_commit(bool sqlite, bool working, bool delete, string change)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var frame = change == "delete" ? await ClaimArtifactRun(f, setup) : null;
        var target = await WorkspaceMutationCase(f, setup, working);
        var before = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var fired = false;
        var hooked = new WorkspaceMutationHook(WorkspaceReader(f), beforeCommit: async () =>
        {
            fired = true;
            var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
            if (change == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, target.Grant.AssignmentId, revision);
            if (change == "logout")
            {
                await f.Identity.SignOutAsync(target.Human.AuthenticationSessionId);
                var next = (await f.Identity.SignInAsync("workspace-mutator@example.test", "strong-operation-password"))!.Session;
                Assert.NotEqual(target.Human.AuthenticationSessionId, next.SessionId);
            }
            if (change == "archive") await f.Agents.SetLifecycleAsync(setup.Owner.PrincipalId, setup.Tenant,
                setup.Agent.Instance.InstanceId, AgentInstanceLifecycle.Archived, revision, setup.Agent.Instance.Revision);
            if (change == "security") await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Unrelated security change", ["agents.read"], revision);
            if (change == "delete")
            {
                await CancelArtifactRun(f, setup, frame!);
                var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Run.SessionId))!;
                await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(setup.Run.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete), revision);
            }
        });
        var service = WorkspaceMutations(f, hooked);
        var error = await Record.ExceptionAsync(() => delete
            ? service.DeleteAsync(setup.Tenant, target.Id, working, target.Human, before, target.Path).AsTask()
            : service.WriteAsync(setup.Tenant, target.Id, working, target.Human, before, target.Path, "replacement"u8.ToArray()).AsTask());
        Assert.True(fired); Assert.NotNull(error);
        Assert.True(error is AccessDeniedException or SecurityConflictException, error.ToString());
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(target.Root, "private.txt")));
        Assert.Equal(new[] { "private.txt" }, Directory.EnumerateFileSystemEntries(target.Root).Select(p => Path.GetFileName(p)!).ToArray());
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.ReasonCode == "human_workspace_authorized");
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_mutations_require_current_audit_evidence_before_any_protected_effect(bool sqlite, bool delete)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var target = await WorkspaceMutationCase(f, setup, true);
        var service = WorkspaceMutations(f, WorkspaceReader(f), new WorkspaceAuditUnavailable());
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var error = await Assert.ThrowsAsync<SecurityConflictException>(() => delete
            ? service.DeleteAsync(setup.Tenant, target.Id, true, target.Human, revision, target.Path).AsTask()
            : service.WriteAsync(setup.Tenant, target.Id, true, target.Human, revision, target.Path, "replacement"u8.ToArray()).AsTask());
        Assert.Equal("stale_revision", error.ReasonCode);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(target.Root, "private.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(target.Root));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_completed_effect_keeps_truthful_acknowledgement_after_later_revocation(bool sqlite, bool delete)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var target = await WorkspaceMutationCase(f, setup, false);
        var store = new WorkspaceMutationHook(WorkspaceReader(f), afterEffect: async () =>
            await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, target.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision));
        var service = WorkspaceMutations(f, store); var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        if (delete) await service.DeleteAsync(setup.Tenant, target.Id, false, target.Human, revision, target.Path);
        else await service.WriteAsync(setup.Tenant, target.Id, false, target.Human, revision, target.Path, "known completed"u8.ToArray());
        var path = Path.Combine(target.Root, "private.txt");
        if (delete) Assert.False(File.Exists(path)); else Assert.Equal("known completed", await File.ReadAllTextAsync(path));
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.ReasonCode == "human_workspace_authorized");
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.WriteAsync(setup.Tenant, target.Id, false, target.Human,
            (f.Iam.ReadTenantAsync(setup.Tenant).Result)!.Tenant.Revision, target.Path, "must deny"u8.ToArray()).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_unconfirmed_effect_is_not_replayed_and_does_not_leak_internal_errors(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var target = await WorkspaceMutationCase(f, setup, true); var calls = 0;
        var service = WorkspaceMutations(f, new WorkspaceMutationHook(WorkspaceReader(f), afterEffect: () =>
        { calls++; throw new IOException("Secret path: " + target.Root); }));
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => service.WriteAsync(setup.Tenant, target.Id, true, target.Human,
            (f.Iam.ReadTenantAsync(setup.Tenant).Result)!.Tenant.Revision, target.Path, "possibly completed"u8.ToArray()).AsTask());
        Assert.Equal("workspace_effect_unknown", error.Code); Assert.DoesNotContain(target.Root, error.Message); Assert.Equal(1, calls);
        Assert.Equal("possibly completed", await File.ReadAllTextAsync(Path.Combine(target.Root, "private.txt")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_write_freezes_supplied_bytes_and_rejects_root_alias_and_foreign_owner(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var target = await WorkspaceMutationCase(f, setup, false);
        var bytes = "exact supplied"u8.ToArray(); var service = WorkspaceMutations(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () =>
        { Array.Fill(bytes, (byte)'x'); return Task.CompletedTask; }));
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        await service.WriteAsync(setup.Tenant, target.Id, false, target.Human, revision, target.Path, bytes);
        Assert.Equal("exact supplied", await File.ReadAllTextAsync(Path.Combine(target.Root, "private.txt")));
        foreach (var path in new[] { "/home", "/working/private.txt", "/home/../escape.txt", "/home//alias.txt" })
        {
            await Assert.ThrowsAsync<AgentCoreException>(() => service.WriteAsync(setup.Tenant, target.Id, false, target.Human, revision, path, bytes).AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(() => service.DeleteAsync(setup.Tenant, target.Id, false, target.Human, revision, path).AsTask());
        }
        await Assert.ThrowsAsync<AccessDeniedException>(() => WorkspaceReader(f).WriteTenantAsync(new(f.Ids.NewId(), target.Id, null), target.Path, bytes, _ => ValueTask.CompletedTask).AsTask());
        Assert.Equal("exact supplied", await File.ReadAllTextAsync(Path.Combine(target.Root, "private.txt")));
    }

    [Theory]
    [InlineData(false, TenantSessionAction.Pause)] [InlineData(true, TenantSessionAction.Pause)]
    [InlineData(false, TenantSessionAction.Archive)] [InlineData(true, TenantSessionAction.Archive)]
    [InlineData(false, TenantSessionAction.Complete)] [InlineData(true, TenantSessionAction.Complete)]
    public async Task Tenant_workspace_working_mutations_respect_retained_Session_lifecycle(bool sqlite, TenantSessionAction action)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        await CancelArtifactRun(f, setup, frame); var target = await WorkspaceMutationCase(f, setup, true);
        var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Run.SessionId))!;
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(setup.Run.SessionId, current.Snapshot.Revision, action), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var service = WorkspaceMutations(f, WorkspaceReader(f)); var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var effect = service.WriteAsync(setup.Tenant, target.Id, true, target.Human, revision, target.Path, "paused human edit"u8.ToArray()).AsTask();
        if (action == TenantSessionAction.Pause)
        {
            await effect;
            Assert.Equal("paused human edit", await File.ReadAllTextAsync(Path.Combine(target.Root, "private.txt")));
        }
        else
        {
            Assert.Equal("workspace_read_only", (await Assert.ThrowsAsync<SecurityConflictException>(() => effect)).ReasonCode);
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(target.Root, "private.txt")));
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_audit_owner_rejects_forged_human_witness_and_decision(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var target = await WorkspaceMutationCase(f, setup, false);
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var owner = new TenantWorkspaceReference(setup.Tenant, target.Id, null);
        TenantWorkspaceHumanAuthorization Proof(string decision = "allowed") => new(owner, target.Human, revision, setup.Agent.Instance.Revision, "workspaces.write",
            new(f.Ids.NewId(), setup.Tenant, target.Human.PrincipalId, "workspaces.write", TenantWorkspaceRules.Scope(owner).ResourceId!, decision,
                decision == "allowed" ? "human_workspace_authorized" : "human_workspace_denied", DateTimeOffset.FromUnixTimeMilliseconds(f.Clock.GetUtcNow().ToUnixTimeMilliseconds())));
        var audits = (ITenantAuthorizationAuditStore)f.Iam;
        var valid = Proof(); Assert.True(await audits.TryRecordWorkspaceAuthorizationAsync(valid));
        Assert.True(await audits.TryRecordWorkspaceAuthorizationAsync(valid));
        Assert.False(await audits.TryRecordWorkspaceAuthorizationAsync(Proof() with { SecurityRevision = revision - 1 }));
        Assert.False(await audits.TryRecordWorkspaceAuthorizationAsync(Proof() with { InstanceRevision = setup.Agent.Instance.Revision + 1 }));
        Assert.False(await audits.TryRecordWorkspaceAuthorizationAsync(Proof() with { Human = target.Human with { AuthenticationSessionId = f.Ids.NewId() } }));
        Assert.False(await audits.TryRecordWorkspaceAuthorizationAsync(Proof("denied")));
        await Assert.ThrowsAsync<ArgumentException>(() => audits.TryRecordWorkspaceAuthorizationAsync(Proof() with { Permission = "workspaces.read" }).AsTask());
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, target.Grant.AssignmentId, revision);
        revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        Assert.False(await audits.TryRecordWorkspaceAuthorizationAsync(Proof()));
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.ReasonCode == "human_workspace_authorized");
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(target.Root, "private.txt")));
    }

    private static async Task<(TenantSessionInitiator Human, RoleAssignment Grant, Guid Id, string Path, string Root)> WorkspaceMutationCase(Fixture f, Environment setup, bool working)
    {
        _ = WorkspaceReader(f);
        var human = await User(f, setup, "workspace-mutator@example.test", ["agents.read", "sessions.read"]);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Exact workspace mutations", ["workspaces.write", "workspaces.delete"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, human.PrincipalId, role.RoleId,
            working ? TenantWorkspaceRules.Working(setup.Tenant, setup.Run.SessionId) : TenantWorkspaceRules.Home(setup.Tenant, setup.Agent.Instance.InstanceId), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var root = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), setup.Agent.Instance.InstanceId, setup.Run.SessionId)
            : AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), setup.Agent.Instance.InstanceId);
        var path = (working ? "/working" : "/home") + "/private.txt";
        await new CanonicalWorkspaceFilesystem(root, working ? "/working" : "/home").WriteAsync(path, "original"u8.ToArray(), 1024, 4096);
        return (human, grant, working ? setup.Run.SessionId : setup.Agent.Instance.InstanceId, path, root);
    }
    private sealed class WorkspaceMutationHook(ITenantWorkspaceMutationStore inner, Func<Task>? beforeCommit = null, Func<Task>? afterEffect = null, Func<Task>? beforeEffect = null) : ITenantWorkspaceMutationStore
    {
        private async ValueTask Commit(Func<CancellationToken, ValueTask> authorize, CancellationToken ct)
        { if (beforeCommit is not null) await beforeCommit(); await authorize(ct); }
        public async ValueTask WriteTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
        { if (beforeEffect is not null) await beforeEffect(); await inner.WriteTenantAsync(owner, path, bytes, token => Commit(authorizeCommit, token), ct); if (afterEffect is not null) await afterEffect(); }
        public async ValueTask DeleteTenantAsync(TenantWorkspaceReference owner, string path, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
        { await inner.DeleteTenantAsync(owner, path, token => Commit(authorizeCommit, token), ct); if (afterEffect is not null) await afterEffect(); }
    }
    private sealed class WorkspaceAuditUnavailable : ITenantAuthorizationAuditStore
    {
        public ValueTask<bool> TryRecordWorkspaceAuthorizationAsync(TenantWorkspaceHumanAuthorization proof, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<bool> TryRecordOperationAuthorizationAsync(AuthorizationAudit audit, CancellationToken ct = default) => ValueTask.FromResult(false);
    }
}
