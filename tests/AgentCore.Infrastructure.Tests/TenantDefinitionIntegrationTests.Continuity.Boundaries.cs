using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    private static async Task ChangeHistory(Fixture f, MemorySetup owner, Guid session, bool delete = false)
    {
        var saved = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.Tenant, session))!;
        await owner.Sessions.ChangeAsync(owner.Tenant, new(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId),
            new(session, saved.Snapshot.Revision, delete ? TenantSessionAction.Delete : TenantSessionAction.Rename, delete ? null : "CHANGED_SOURCE_NO_SUBSTITUTION"), (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
    }
    [Theory] [InlineData(false, "source")] [InlineData(true, "source")] [InlineData(false, "delete")] [InlineData(true, "delete")]
    [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    public async Task Tenant_continuity_post_decision_changes_or_refusal_never_deliver_saved_historical_bytes(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); await MemoryPolicy(f, owner, "sessions.read", OperationApprovalMode.Always); var s = ContinuityServices(f);
        var request = await PrepareContinuityReview(f, current.Frame, HistoryCall(source.Frame.SessionId), s.Approvals); var frame = await DecideMemoryReview(f, owner, request, s.Approvals, boundary != "reject");
        if (boundary is "source" or "delete") await ChangeHistory(f, owner, source.Frame.SessionId, boundary == "delete");
        if (boundary == "read") await MemoryPermissions(f, owner, "agents.read", "agents.chat", "memory.read");
        var outcome = boundary == "reject" ? (await s.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, frame.RunId))!)).Outcome!.Outcome
            : (await s.Effects.ExecuteAsync(new(owner.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash)).Outcome;
        Assert.NotEqual(TenantOperationOutcome.Completed, outcome); Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.Tenant, 200), a => a.Action == "operations.continuity.read" && a.Decision == "allowed");
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(owner.Tenant, request.RequestId));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_continuity_source_change_disables_preview_and_decision_without_substituting_current_excerpts(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); await MemoryPolicy(f, owner, "sessions.read", OperationApprovalMode.Always); var s = ContinuityServices(f);
        var request = await PrepareContinuityReview(f, current.Frame, HistoryCall(source.Frame.SessionId), s.Approvals); await ChangeHistory(f, owner, source.Frame.SessionId);
        var actor = new TenantSessionInitiator(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId); var view = await s.Approvals.ReadViewAsync(owner.Tenant, request.RequestId, actor);
        Assert.False(view.Review!.CanDecide); Assert.True(view.Review.CanCancel); Assert.Equal("continuity_source_changed", view.Review.DecisionUnavailableReason);
        Assert.Contains(view.Review.Changes, c => c.Field == "Current observation"); Assert.DoesNotContain("CHANGED_SOURCE_NO_SUBSTITUTION", JsonSerializer.Serialize(view.Review.Changes));
        await Assert.ThrowsAsync<SecurityConflictException>(() => s.Approvals.DecideAsync(owner.Tenant, request.RequestId, actor, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision).AsTask());
        Assert.Equal(TenantApprovalStatus.Pending, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(owner.Tenant, request.RequestId))!.Status);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_continuity_scoped_history_is_filtered_before_discovery_and_cannot_cross_Agent_or_current_Session(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, operations: true, memoryEnabled: false); var hidden = await MemoryClaim(f, owner); await MemoryCommit(f, hidden, await MemoryPlan(f, hidden));
        var readable = await MemoryClaim(f, owner); await MemoryCommit(f, readable, await MemoryPlan(f, readable)); var current = await MemoryClaim(f, owner);
        await MemoryPermissions(f, owner, "agents.read", "agents.chat");
        var role = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Exact historical reader", ["sessions.read"], (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        foreach (var id in new[] { current.Frame.SessionId, readable.Frame.SessionId }) await f.Access.AssignRoleAsync(owner.Human.PrincipalId, owner.Tenant, owner.Agent.PrincipalId, role.RoleId, TenantSessionRules.Scope(owner.Tenant, id), (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var s = ContinuityServices(f); var result = await s.Reads.InspectAsync(current.Frame, new("search", ToolCatalog.ContinuitySearch, "{\"query\":\"evidence\",\"limit\":10}"), 6000);
        using var json = JsonDocument.Parse(result); Assert.Equal(readable.Frame.SessionId, Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid()); Assert.DoesNotContain(hidden.Frame.SessionId.ToString("D"), result);
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Reads.InspectAsync(current.Frame, HistoryCall(hidden.Frame.SessionId), 6000).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Reads.InspectAsync(current.Frame, HistoryCall(Guid.NewGuid()), 6000).AsTask());
        // Restoring broad read still cannot adopt a different Agent's historical Session.
        await MemoryPermissions(f, owner, "agents.read", "agents.chat", "sessions.read");
        var otherAgent = await new TenantAgentService(f.Iam, (ITenantAgentStore)f.Iam, new SessionNoBuiltinFallback(), f.Authorizer, f.Ids, TimeProvider.System, ownedDefinitions: f.Definitions)
            .CreateFromPublicationAsync(owner.Human.PrincipalId, owner.Tenant, owner.Definition, 1, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(owner.Human.PrincipalId, owner.Tenant, otherAgent.PrincipalId, owner.Role, AccessScope.ForTenant(owner.Tenant), (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var other = await MemoryClaim(f, owner, otherAgent: otherAgent); await MemoryCommit(f, other, await MemoryPlan(f, other));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Reads.InspectAsync(current.Frame, HistoryCall(other.Frame.SessionId), 6000).AsTask());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_continuity_checkpoint_retains_source_fences_through_compaction_and_blocks_changed_source_publication(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); var s = ContinuityServices(f); var body = await s.Reads.InspectAsync(current.Frame, HistoryCall(source.Frame.SessionId), 6000);
        var runtime = (ITenantRuntimeStore)f.Iam; var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, body, ToolCallId: "history", Name: ToolCatalog.ContinuityGet)]), 1, 0, 180000, 0);
        async Task<AgentRun> Save(AgentRunCheckpoint cp) { var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, current.Frame.RunId))!; return await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, DateTimeOffset.UtcNow, current.Frame.Generation, cp, null), f.Ids.NewId()); }
        var saved = await Save(checkpoint); var empty = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([]), 1, 0, 180000, 0);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Save(empty));
        var proofs = TenantHistoricalReferences.Read(saved.Checkpoint);
        var compact = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([], executionState: new(TenantHistoricalSources: proofs)), 1, 0, 180000, 0); saved = await Save(compact); Assert.Equal(proofs, TenantHistoricalReferences.Read(saved.Checkpoint));
        var plan = await MemoryPlan(f, current);
        await ChangeHistory(f, owner, source.Frame.SessionId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantContinuityStore)f.Iam).RequireTenantHistoricalSourcesAsync(new(owner.Tenant, current.Frame.RunId), proofs, current.Frame.Generation).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => Save(compact));
        await Assert.ThrowsAsync<AccessDeniedException>(() => MemoryCommit(f, current, plan));
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, current.Frame.RunId))!.Status);
    }
    [Fact]
    public async Task Tenant_continuity_SQLite_upgrade_preserves_owned_history_and_facts_and_freezes_prepared_sources()
    {
        await using var f = await Fixture.Create(true); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source, Learned())); var current = await MemoryClaim(f, owner);
        await using (var historical = await HistoricalTenantDatabase.CreateAsync(f.Contexts!,
            "20261021185601_TenantMemoryOperations", HistoricalTenantDatabase.MemoryAdmissionTables, [source.Run]))
        {
            var before = await historical.Db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger'").ToArrayAsync();
            await historical.Db.GetService<IMigrator>().MigrateAsync("20261021201045_TenantContinuityRead");
            Assert.Equal(3, await historical.Db.Set<TenantLearnedMemoryRecord>().CountAsync());
            var after = await historical.Db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger'").ToArrayAsync();
            Assert.All(before, name => Assert.Contains(name, after));
        }
        await using var db = f.Contexts!.CreateDbContext(); var migrations = db.GetService<IMigrator>();
        await MemoryPolicy(f, owner, "sessions.read", OperationApprovalMode.Always); var s = ContinuityServices(f); var request = await PrepareContinuityReview(f, current.Frame, HistoryCall(source.Frame.SessionId), s.Approvals);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.ContinuityRead.SessionsJson','[]') WHERE RequestId={request.RequestId}"));
        await HistoricalTenantDatabase.AssertApprovalDowngradeRefusedAsync(f.Contexts!,
            "20261021201045_TenantContinuityRead", "20261021185601_TenantMemoryOperations", "Rows=0",
            HistoricalTenantDatabase.MemoryAdmissionTables, [source.Run]);
        Assert.True(await db.Set<TenantApprovalRecord>().AnyAsync(r => r.RequestId == request.RequestId));
        // The isolated historical refusal leaves the current source usable.
        await migrations.MigrateAsync();
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var reopened = ContinuityServices(f, new SqliteIamStore(f.Contexts, f.Content)); var view = await reopened.Approvals.ReadViewAsync(owner.Tenant, request.RequestId, new(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId)); Assert.True(view.Review!.CanDecide);
    }
}
