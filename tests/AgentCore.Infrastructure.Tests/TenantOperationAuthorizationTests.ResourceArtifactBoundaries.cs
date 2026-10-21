using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    [InlineData(false, "resources.read")] [InlineData(true, "resources.read")]
    public async Task Tenant_resource_Artifact_requires_every_source_read_before_byte_IO(bool sqlite, string missing)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, new[] { "sessions.read", "agents.read", "resources.read", "artifacts.create" }.Where(k => k != missing).ToArray());
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent);
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => SourcePublicationService(f, resourceSources: ResourceSources(f, probe))
            .ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "missing-source-read", JsonSerializer.Serialize(new { path = v.Path, displayName = "copy.txt" })).AsTask())).ReasonCode);
        Assert.Equal(0, probe.Reads); Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_Artifact_commit_refuses_changed_binding_or_revoked_authority(bool sqlite, bool revoke)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); var reads = await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var gate = new ArtifactCommitGate((ITenantArtifactOperationStore)f.Iam);
        var pending = SourcePublicationService(f, gate).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "resource-race", JsonSerializer.Serialize(new { path = v.Path, displayName = "copy.txt" })).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (revoke) await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, reads.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            else await f.AgentConfiguration().UpsertResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision,
                new(Guid.Parse(v.Key[9..]), 1, "source.txt", AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, "text/plain", "Changed source"u8.ToArray()), initiator: s.Owner);
        }
        finally { gate.Proceed.TrySetResult(); }
        var error = await Record.ExceptionAsync(async () => await pending); Assert.True(error is AccessDeniedException or SecurityConflictException, error?.ToString());
        Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, frame.RunId, "resource-race"));
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_Artifact_inbox_filters_actual_child_before_counts_and_qualifies_approval_scope(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, resource: true); var s = v.Setup;
        var reader = await User(f, s, "resource-export-reader@example.test", ["sessions.read", "agents.read", "artifacts.create", "approvals.read", "workspaces.read"]);
        Assert.Equal(0, (await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1))).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader).AsTask());
        var key = v.Request.Operation.ArtifactPublication!.Source!.ResourceKey!; var child = TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", key);
        async Task<RoleAssignment> Allow(string permission, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Exact resource " + permission, [permission], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            return await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        }
        var raw = await Allow("resources.read", child); Assert.Equal(1, (await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1))).Total);
        await Allow("approvals.approve", TenantSessionRules.Scope(s.Tenant, v.Frame.SessionId));
        Assert.False((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader)).Review!.CanDecide);
        await Allow("approvals.approve", child); Assert.True((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader)).Review!.CanDecide);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, raw.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.Equal(0, (await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1))).Total);
        await f.AgentConfiguration().ChangeResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId, key, true, null,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision, 1, initiator: s.Owner);
        Assert.Equal(0, (await v.Approvals.ListAsync(s.Tenant, s.Owner, new(0, 1))).Total);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_Artifact_refusal_is_not_erased_by_policy_relaxation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, resource: true); var s = v.Setup;
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, false, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        await v.Approvals.DispatchPendingAsync();
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "resources.read", new(OperationApprovalMode.None, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentCore.Application.Execution.AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var direct = await SourcePublicationService(f).ExecuteWorkspaceAsync(new(s.Tenant, run.AgentRunId), run.Claim!.Generation, "repeat-rejected-export", v.Arguments);
        Assert.Equal("previously_rejected", direct.ReasonCode); Assert.Null(direct.Receipt);
        Assert.Equal("previously_rejected", (await v.Approvals.PrepareWorkspaceArtifactAsync(new(s.Tenant, run.AgentRunId), v.Arguments, run.Revision, run.Claim.Generation)).ReasonCode);
    }

    [Fact]
    public async Task Tenant_resource_Artifact_SQLite_shape_rollback_and_reopen_preserve_exact_source()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, resource: true);
        foreach (var change in new[] { "key", "revision", "working", "path", "mixed", "source" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == v.Request.RequestId);
            var json = JsonNode.Parse(row.PayloadJson)!; var source = json["Operation"]!["ArtifactPublication"]!["Source"]!;
            if (change == "key") source["ResourceKey"] = null;
            if (change == "revision") source["ResourceRevision"] = -1;
            if (change == "working") source["Working"] = true;
            if (change == "path") source["Path"] = "/home/substitute.txt";
            if (change == "mixed") json["Operation"]!["WorkspaceRead"] = new JsonObject();
            if (change == "source") source["ResourceKey"] = "foreign:invalid-key";
            row.PayloadJson = json.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); var own = Array.FindIndex(migrations, m => m.EndsWith("TenantArtifactResourceSourceReview", StringComparison.Ordinal)); Assert.True(own > 0);
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[own - 1])); await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        }
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(v.Setup.Tenant, v.Request.RequestId));
    }
}
