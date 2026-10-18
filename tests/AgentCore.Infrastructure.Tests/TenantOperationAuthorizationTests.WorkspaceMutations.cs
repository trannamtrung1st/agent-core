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
        var scope = working ? TenantWorkspaceRules.Working(setup.Tenant, setup.Run.SessionId) : TenantWorkspaceRules.Home(setup.Tenant, setup.Agent.Instance.InstanceId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Write exact workspace", ["workspaces.write"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, writer.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var id = working ? setup.Run.SessionId : setup.Agent.Instance.InstanceId; var logical = working ? "/working" : "/home";
        var physical = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), setup.Agent.Instance.InstanceId, setup.Run.SessionId)
            : AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), setup.Agent.Instance.InstanceId);
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
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
    private static TenantWorkspaceMutationService WorkspaceMutations(Fixture f, ITenantWorkspaceMutationStore store, ITenantAuthorizationAuditStore? audit = null) =>
        new(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, store, audit ?? (ITenantAuthorizationAuditStore)f.Iam, f.Identity, f.Authorizer, f.Ids, f.Clock);
}
