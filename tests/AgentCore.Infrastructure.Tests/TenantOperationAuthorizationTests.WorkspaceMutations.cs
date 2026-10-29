using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Workspaces;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_mutations_use_independent_write_delete_without_granting_body_read_and_audit_exact_current_actor(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var store = WorkspaceReader(f); var service = WorkspaceMutations(f, store);
        var writer = await User(f, setup, "workspace-writer@example.test", ["agents.read", "sessions.read"]);
        if (!working) await GrantExactSpaceRead(f, setup, writer.PrincipalId, SpaceId(setup));
        var scope = working ? TenantWorkspaceRules.Working(setup.Tenant, setup.Run.SessionId) : TenantWorkspaceRules.Home(setup.Tenant, SpaceId(setup));
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Write exact workspace", ["workspaces.write"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, writer.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var id = working ? setup.Run.SessionId : SpaceId(setup); var logical = working ? "/working" : "/home";
        var physical = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), setup.Agent.Instance.InstanceId, setup.Run.SessionId)
            : AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), setup.Tenant, SpaceId(setup));
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var hints = await service.InspectAsync(setup.Tenant, id, working, writer);
        Assert.False(hints.CanRead); Assert.True(hints.CanWrite); Assert.False(hints.CanDelete); Assert.False(hints.ReadOnly);
        Assert.Equal(revision, hints.SecurityRevision); Assert.Equal(id, hints.Workspace.OwnerId);
        await service.AuthorizeAsync(setup.Tenant, id, working, writer, revision, false); Assert.False(Directory.Exists(physical));
        const string name = "/private-secret-name.txt"; var bytes = Encoding.UTF8.GetBytes("Private supplied bytes thử nghiệm");
        await service.WriteAsync(setup.Tenant, id, working, writer, revision, logical + name, bytes);
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(bytes, await new CanonicalWorkspaceFilesystem(physical, logical).ReadAsync(logical + name, 1024));
        await Assert.ThrowsAsync<AccessDeniedException>(() => WorkspaceService(f, store).ReadAsync(setup.Tenant, id, working, writer, logical + name).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.DeleteAsync(setup.Tenant, id, working, writer, revision, logical + name).AsTask());
        Assert.True(File.Exists(Path.Combine(physical, name.TrimStart('/'))));
        role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Delete exact workspace", ["workspaces.delete"], revision);
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, writer.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await service.DeleteAsync(setup.Tenant, id, working, writer, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, logical + name);
        Assert.False(File.Exists(Path.Combine(physical, name.TrimStart('/'))));
        var audit = (await f.Iam.ReadAuditAsync(setup.Tenant, 200)).Where(a => a.ReasonCode.StartsWith("human_workspace_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, audit.Length); Assert.Equal(2, audit.Count(a => a.Decision == "allowed")); Assert.Single(audit, a => a.Decision == "denied");
        Assert.All(audit, a => { Assert.Equal(writer.PrincipalId, a.ActorPrincipalId); Assert.Equal(scope.ResourceId, a.Target); Assert.Null(a.Execution); });
        var json = System.Text.Json.JsonSerializer.Serialize(audit); Assert.DoesNotContain(name, json); Assert.DoesNotContain("Private supplied", json);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_hints_require_the_exact_parent_and_do_not_project_live_Instance_fields_through_Session_read(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var reader = await User(f, setup, "session-only-workspace@example.test", ["sessions.read"]);
        var service = WorkspaceMutations(f, WorkspaceReader(f));
        var hints = await service.InspectAsync(setup.Tenant, setup.Run.SessionId, true, reader);
        Assert.Equal(hints.SessionTitle, hints.WorkspaceName); Assert.True(hints.OwnerRevision >= 1);
        Assert.False(hints.CanRead); Assert.False(hints.CanWrite); Assert.False(hints.CanDelete);
        Assert.Equal(setup.Run.SessionId, hints.Workspace.OwnerId);
        Assert.Equal(setup.Run.SessionId, hints.Workspace.WorkingSessionId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.InspectAsync(setup.Tenant, SpaceId(setup), false, reader).AsTask());
        Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(f.Root, "workspaces"), setup.Agent.Instance.InstanceId)));
    }

    private static TenantWorkspaceMutationService WorkspaceMutations(Fixture f, ITenantWorkspaceMutationStore store, ITenantAuthorizationAuditStore? audit = null) =>
        new(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, store, audit ?? (ITenantAuthorizationAuditStore)f.Iam, f.Identity, f.Authorizer, f.Ids, f.Clock);
}
