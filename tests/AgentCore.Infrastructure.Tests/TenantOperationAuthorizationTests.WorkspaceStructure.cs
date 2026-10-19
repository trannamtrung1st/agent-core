using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Workspaces;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static async ValueTask WorkspacePrimitiveAuthority(Fixture f, Environment s, IEnumerable<TenantWorkspaceReference> owners, CancellationToken ct)
    {
        var state = (await f.Iam.ReadTenantAsync(s.Tenant, ct))!;
        foreach (var owner in owners)
        {
            var targets = new[] { (owner.SessionId is null ? "agents.read" : "sessions.read", owner.SessionId is { } id
                ? TenantSessionRules.Scope(s.Tenant, id) : AccessScope.ForResource(s.Tenant, "agent", owner.InstanceId.ToString("D"))),
                ("workspaces.read", TenantWorkspaceRules.Scope(owner)), ("workspaces.write", TenantWorkspaceRules.Scope(owner)), ("workspaces.delete", TenantWorkspaceRules.Scope(owner)) };
            foreach (var (key, scope) in targets)
                if (!(await f.Authorizer.CheckOwnedTenantAsync(state, s.Agent.PrincipalId, PermissionCatalog.Get(key), scope, ct)).Allowed)
                    throw new AccessDeniedException("permission_denied");
        }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_primitives_patch_and_structure_actual_owned_files_then_copy_across_current_roots(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write", "workspaces.delete"]);
        var files = WorkspaceReader(f); var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId, working ? s.Run.SessionId : null);
        var other = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId, working ? null : s.Run.SessionId);
        ValueTask Gate(CancellationToken ct) => WorkspacePrimitiveAuthority(f, s, [owner], ct);
        var root = owner.LogicalRoot;
        await WorkspaceMutations(f, files).WriteAsync(s.Tenant, working ? s.Run.SessionId : s.Agent.Instance.InstanceId, working,
            s.Owner, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, root + "/note.txt", Encoding.UTF8.GetBytes("original: thử nghiệm"));
        var patched = await files.PatchTenantAsync(owner, root + "/note.txt", [new("original", "updated")], Gate);
        Assert.Equal(1, patched.EditsApplied); Assert.DoesNotContain("thử nghiệm", JsonSerializer.Serialize(patched));
        var batch = await files.StructureTenantAsync(owner, [new("mkdir", Path: root + "/tree/empty"),
            new("copy", Source: root + "/note.txt", Destination: root + "/tree/copy.txt"),
            new("move", Source: root + "/tree/copy.txt", Destination: root + "/tree/final.txt"), new("delete", Path: root + "/note.txt")], Gate);
        Assert.True(batch.Completed); Assert.Equal(4, batch.CompletedCount);
        Assert.Equal("updated: thử nghiệm", Encoding.UTF8.GetString((await files.InspectTenantAsync(owner, root + "/tree/final.txt", 1024)).Bytes));
        await files.CopyTenantAsync(owner, root + "/tree", other, other.LogicalRoot + "/published-copy", token => WorkspacePrimitiveAuthority(f, s, [owner, other], token));
        Assert.Equal("updated: thử nghiệm", Encoding.UTF8.GetString((await files.InspectTenantAsync(other, other.LogicalRoot + "/published-copy/final.txt", 1024)).Bytes));
        Assert.Contains((await files.ListTenantAsync(other, other.LogicalRoot + "/published-copy", null, 100)).Items, i => i.Directory && i.LogicalPath.EndsWith("/empty"));
        var failed = await files.StructureTenantAsync(other, [new("copy", Source: other.LogicalRoot + "/published-copy", Destination: other.LogicalRoot + "/published-copy"),
            new("mkdir", Path: other.LogicalRoot + "/not-executed")], token => WorkspacePrimitiveAuthority(f, s, [other], token));
        Assert.False(failed.Completed); Assert.Equal(0, failed.CompletedCount); Assert.Equal("notExecuted", failed.Results[1].Status);
        Assert.DoesNotContain((await files.ListTenantAsync(other, other.LogicalRoot, null, 100)).Items, i => i.LogicalPath.EndsWith("/not-executed"));
    }
}
