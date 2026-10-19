using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Workspaces;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_primitives_first_copy_authority_loss_reports_retained_preparation_as_possible_mutation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var files = WorkspaceReader(f); var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId);
        var physical = AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), owner.InstanceId);
        await new CanonicalWorkspaceFilesystem(physical, "/home").WriteAsync("/home/source.txt", "source"u8.ToArray(), 1024, 4096);
        var fired = false;
        ValueTask Gate(CancellationToken ct)
        {
            if (Directory.Exists(Path.Combine(physical, "prepared-parent"))) { fired = true; throw new AccessDeniedException("permission_denied"); }
            return ValueTask.CompletedTask;
        }
        var result = await files.StructureTenantAsync(owner, [new("copy", Source: "/home/source.txt", Destination: "/home/prepared-parent/copied.txt")], Gate);
        Assert.True(fired); Assert.False(result.Completed); Assert.Equal(0, result.CompletedCount); Assert.True(result.MutationsMayHaveOccurred);
        Assert.True(Directory.Exists(Path.Combine(physical, "prepared-parent"))); Assert.False(File.Exists(Path.Combine(physical, "prepared-parent", "copied.txt")));
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(physical, "source.txt")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_primitives_copy_rechecks_authority_after_preparing_destination_without_copying_source(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var grant = await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write", "workspaces.delete"]);
        var files = WorkspaceReader(f); var sourceOwner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId, s.Run.SessionId); var destinationOwner = sourceOwner with { SessionId = null };
        var home = AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), sourceOwner.InstanceId);
        var working = AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), sourceOwner.InstanceId, s.Run.SessionId);
        await new CanonicalWorkspaceFilesystem(working, "/working").WriteAsync("/working/source.txt", "private literal source"u8.ToArray(), 4096, 4096);
        var fired = false;
        async ValueTask Gate(CancellationToken ct)
        {
            if (!fired && Directory.Exists(home))
            { fired = true; await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant, ct))!.Tenant.Revision, ct); }
            await WorkspacePrimitiveAuthority(f, s, [sourceOwner, destinationOwner], ct);
        }
        await Assert.ThrowsAsync<AccessDeniedException>(() => files.CopyTenantAsync(sourceOwner, "/working/source.txt", destinationOwner, "/home/copy.txt", Gate).AsTask());
        Assert.True(fired); Assert.Empty(Directory.EnumerateFileSystemEntries(home)); Assert.Equal("private literal source", await File.ReadAllTextAsync(Path.Combine(working, "source.txt")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_primitives_copy_preserves_links_without_following_or_disclosing_external_targets(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var files = WorkspaceReader(f); var sourceOwner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId); var destinationOwner = sourceOwner with { SessionId = s.Run.SessionId };
        var home = AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), sourceOwner.InstanceId);
        var native = new CanonicalWorkspaceFilesystem(home, "/home"); native.Ensure(); native.Mkdir("/home/tree/empty");
        var outside = Path.Combine(f.Root, "outside-private.txt"); await File.WriteAllTextAsync(outside, "unavailable external secret");
        File.CreateSymbolicLink(Path.Combine(home, "tree", "outside"), outside);
        await files.CopyTenantAsync(sourceOwner, "/home/tree", destinationOwner, "/working/copied", _ => ValueTask.CompletedTask);
        var page = await files.ListTenantAsync(destinationOwner, "/working/copied", null, 100); Assert.Null(page.Items.Single(i => i.LogicalPath.EndsWith("/outside")).LinkTarget);
        await Assert.ThrowsAsync<AgentCoreException>(() => files.InspectTenantAsync(destinationOwner, "/working/copied/outside", 1024).AsTask());
        Assert.DoesNotContain(outside, System.Text.Json.JsonSerializer.Serialize(page)); Assert.Equal("unavailable external secret", await File.ReadAllTextAsync(outside));
        Assert.Contains(page.Items, i => i.Directory && i.LogicalPath.EndsWith("/empty"));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_primitives_patch_staging_rejects_revocation_or_changed_bytes_without_overwriting_source(bool sqlite, bool sourceChange)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var grant = await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write", "workspaces.delete"]);
        var files = WorkspaceReader(f); var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId, s.Run.SessionId);
        var physical = AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), owner.InstanceId, owner.SessionId!.Value);
        var native = new CanonicalWorkspaceFilesystem(physical, "/working"); await native.WriteAsync("/working/source.txt", "original"u8.ToArray(), 4096, 4096);
        var fired = false;
        async ValueTask Gate(CancellationToken ct)
        {
            if (!fired && Directory.EnumerateFiles(physical).Any(p => p.EndsWith(".write", StringComparison.Ordinal)))
            {
                fired = true;
                if (sourceChange) await File.WriteAllTextAsync(Path.Combine(physical, "source.txt"), "external current writer", ct);
                else await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant, ct))!.Tenant.Revision, ct);
            }
            await WorkspacePrimitiveAuthority(f, s, [owner], ct);
        }
        var error = await Record.ExceptionAsync(() => files.PatchTenantAsync(owner, "/working/source.txt", [new("original", "replacement")], Gate).AsTask());
        Assert.True(fired); Assert.NotNull(error); Assert.True(sourceChange ? error is SecurityConflictException : error is AccessDeniedException, error.ToString());
        Assert.Equal(sourceChange ? "external current writer" : "original", await File.ReadAllTextAsync(Path.Combine(physical, "source.txt")));
        Assert.Equal(new[] { "source.txt" }, Directory.EnumerateFiles(physical).Select(Path.GetFileName));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_primitives_ordered_batch_stops_at_new_authority_loss_and_retains_completed_prefix(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var grant = await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write", "workspaces.delete"]);
        var files = WorkspaceReader(f); var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId);
        var physical = AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), owner.InstanceId); var fired = false;
        async ValueTask Gate(CancellationToken ct)
        {
            if (!fired && Directory.Exists(Path.Combine(physical, "first")))
            { fired = true; await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant, ct))!.Tenant.Revision, ct); }
            await WorkspacePrimitiveAuthority(f, s, [owner], ct);
        }
        var result = await files.StructureTenantAsync(owner, [new("mkdir", Path: "/home/first"), new("mkdir", Path: "/home/second"), new("delete", Path: "/home/first")], Gate);
        Assert.True(fired); Assert.False(result.Completed); Assert.Equal(1, result.CompletedCount); Assert.Equal(1, result.FailedIndex); Assert.True(result.MutationsMayHaveOccurred);
        Assert.Equal("workspace_authority_changed", result.ErrorCode); Assert.Equal(new[] { "completed", "failed", "notExecuted" }, result.Results.Select(r => r.Status));
        Assert.True(Directory.Exists(Path.Combine(physical, "first"))); Assert.False(Directory.Exists(Path.Combine(physical, "second")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_primitives_validate_all_scope_and_shape_before_callback_or_root_creation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var files = WorkspaceReader(f); var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId);
        var physical = AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), owner.InstanceId); var called = false;
        ValueTask Gate(CancellationToken ct) { called = true; throw new AccessDeniedException("permission_denied"); }
        foreach (var bad in new[] { new WorkspaceStructuralOperation("delete", Path: "/working/foreign"), new("delete", Path: "/home"),
            new("mkdir", Path: "/home/*.txt"), new("move", Source: "/home/a", Destination: "/home/../b"), new("unknown", Path: "/home/a") })
            await Assert.ThrowsAsync<AgentCoreException>(() => files.StructureTenantAsync(owner, [new("mkdir", Path: "/home/must-not-exist"), bad], Gate).AsTask());
        Assert.False(called); Assert.False(Directory.Exists(physical));
        var denied = await files.StructureTenantAsync(owner, [new("mkdir", Path: "/home/denied")], Gate);
        Assert.True(called); Assert.False(denied.Completed); Assert.False(denied.MutationsMayHaveOccurred); Assert.False(Directory.Exists(physical));
        var otherSession = await f.Sessions.CreateAsync(s.Tenant, owner.InstanceId, s.Owner, SessionMode.Text, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision);
        foreach (var foreign in new[] { owner with { TenantId = f.Ids.NewId() }, owner with { InstanceId = f.Ids.NewId() } })
            await Assert.ThrowsAsync<AccessDeniedException>(() => files.CopyTenantAsync(owner, "/home/a", foreign, "/home/b", _ => ValueTask.CompletedTask).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => files.CopyTenantAsync(owner with { SessionId = s.Run.SessionId }, "/working/a",
            owner with { SessionId = otherSession.Owner.SessionId }, "/working/b", _ => ValueTask.CompletedTask).AsTask());
        Assert.False(Directory.Exists(physical));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_primitives_recheck_lifecycle_after_async_callback_and_freeze_batch_input(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var files = WorkspaceReader(f); var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId);
        var operations = new WorkspaceStructuralOperation[] { new("mkdir", Path: "/home/literal") };
        var result = await files.StructureTenantAsync(owner, operations, _ => { operations[0] = new("mkdir", Path: "/home/substituted"); return ValueTask.CompletedTask; });
        Assert.True(result.Completed); var physical = AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), owner.InstanceId);
        Assert.True(Directory.Exists(Path.Combine(physical, "literal"))); Assert.False(Directory.Exists(Path.Combine(physical, "substituted")));
        result = await files.StructureTenantAsync(owner, [new("mkdir", Path: "/home/archived-write")], async ct => {
            await f.Agents.SetLifecycleAsync(s.Owner.PrincipalId, s.Tenant, owner.InstanceId, AgentInstanceLifecycle.Archived, (await f.Iam.ReadTenantAsync(s.Tenant, ct))!.Tenant.Revision, s.Agent.Instance.Revision, ct);
        });
        Assert.False(result.Completed); Assert.False(result.MutationsMayHaveOccurred); Assert.False(Directory.Exists(Path.Combine(physical, "archived-write")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_primitives_patch_bounds_UTF8_growth_overlap_and_frozen_input(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId);
        var physical = AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), owner.InstanceId); var native = new CanonicalWorkspaceFilesystem(physical, "/home");
        var files = new FileAgentInstanceWorkspaceStore(Path.Combine(f.Root, "workspaces"), f.Instances, f.Sandboxes, maxFileBytes: 7,
            tenantAgents: (ITenantAgentStore)f.Iam, tenantSessions: (ITenantSessionStore)f.Iam);
        await native.WriteAsync("/home/text.txt", Encoding.UTF8.GetBytes("éx"), 7, 4096);
        var quota = await Assert.ThrowsAsync<AgentCoreException>(() => files.PatchTenantAsync(owner, "/home/text.txt", [new("x", "漢漢")], _ => ValueTask.CompletedTask).AsTask());
        Assert.Equal("WorkspaceQuotaExceeded", quota.Code); Assert.Equal("éx", Encoding.UTF8.GetString(await native.ReadAsync("/home/text.txt", 7)));
        await native.WriteAsync("/home/text.txt", "aaa"u8.ToArray(), 7, 4096);
        var overlap = await Assert.ThrowsAsync<AgentCoreException>(() => files.PatchTenantAsync(owner, "/home/text.txt", [new("aa", "x")], _ => ValueTask.CompletedTask).AsTask());
        Assert.Equal("Conflict", overlap.Code); Assert.Equal("aaa", Encoding.UTF8.GetString(await native.ReadAsync("/home/text.txt", 7)));
        await native.WriteAsync("/home/text.txt", new byte[] { 255 }, 7, 4096);
        await Assert.ThrowsAsync<AgentCoreException>(() => files.PatchTenantAsync(owner, "/home/text.txt", [new("x", "y")], _ => ValueTask.CompletedTask).AsTask());
        await native.WriteAsync("/home/text.txt", "safe"u8.ToArray(), 7, 4096);
        var edits = new WorkspaceTextEdit[] { new("safe", "saved") };
        await files.PatchTenantAsync(owner, "/home/text.txt", edits, _ => { edits[0] = new("safe", "forged"); return ValueTask.CompletedTask; });
        Assert.Equal("saved", Encoding.UTF8.GetString(await native.ReadAsync("/home/text.txt", 7)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_primitives_copy_retains_partial_quota_effect_and_never_executes_later_operations(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId);
        var physical = AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(f.Root, "workspaces"), owner.InstanceId); var native = new CanonicalWorkspaceFilesystem(physical, "/home");
        var files = new FileAgentInstanceWorkspaceStore(Path.Combine(f.Root, "workspaces"), f.Instances, f.Sandboxes, maxFileBytes: 2,
            tenantAgents: (ITenantAgentStore)f.Iam, tenantSessions: (ITenantSessionStore)f.Iam);
        await native.WriteAsync("/home/source/large.txt", "oversized"u8.ToArray(), 100, 4096);
        var result = await files.StructureTenantAsync(owner, [new("mkdir", Path: "/home/completed"), new("copy", Source: "/home/source", Destination: "/home/partial"),
            new("mkdir", Path: "/home/must-not-execute")], _ => ValueTask.CompletedTask);
        Assert.False(result.Completed); Assert.Equal(1, result.CompletedCount); Assert.True(result.MutationsMayHaveOccurred); Assert.Equal("WorkspaceQuotaExceeded", result.ErrorCode);
        Assert.True(Directory.Exists(Path.Combine(physical, "completed"))); Assert.True(Directory.Exists(Path.Combine(physical, "partial")));
        Assert.False(Directory.Exists(Path.Combine(physical, "must-not-execute"))); Assert.Equal("oversized", Encoding.UTF8.GetString(await native.ReadAsync("/home/source/large.txt", 100)));
    }
}
