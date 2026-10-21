using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false, "source")] [InlineData(true, "source")] [InlineData(false, "delete")] [InlineData(true, "delete")]
    [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    public async Task Tenant_experience_post_decision_source_changes_and_raw_read_loss_never_deliver_or_record(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); await MemoryPolicy(f, owner, "sessions.read", OperationApprovalMode.Always); var s = ExperienceServices(f);
        var call = new ModelToolCall("source", TenantExperienceToolContract.Source, JsonSerializer.Serialize(new { sourceKind = "Session", sourceId = source.Frame.SessionId }));
        var request = await PrepareExperienceReview(f, current.Frame, call, s.Approvals); var frame = await DecideMemoryReview(f, owner, request, s.Approvals, boundary != "reject");
        if (boundary is "source" or "delete") await ChangeHistory(f, owner, source.Frame.SessionId, boundary == "delete");
        if (boundary == "read") await MemoryPermissions(f, owner, "agents.read", "agents.chat", "memory.read", "memory.manage");
        var outcome = boundary == "reject" ? (await s.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, frame.RunId))!)).Outcome!.Outcome
            : (await s.Effects.ExecuteAsync(new(owner.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash)).Outcome;
        Assert.NotEqual(TenantOperationOutcome.Completed, outcome); Assert.Empty((await ((ITenantExperienceManagementStore)f.Iam).ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new())).Items);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.Tenant, 200), a => a.Action == "operations.experience.source" && a.Decision == "allowed");
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(owner.Tenant, request.RequestId));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_disabled_admission_never_gains_live_opt_in_and_current_opt_out_closes_existing_proofs(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f, enabled: false); var old = await MemoryClaim(f, owner);
        owner = await ExperienceSettings(f, owner, true); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var store = (ITenantExperienceStore)f.Iam;
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ObserveTenantExperienceSourceAsync(new(owner.Tenant, old.Frame.RunId), old.Frame.Generation, ExperienceSourceKind.Session, source.Frame.SessionId).AsTask());
        var current = await MemoryClaim(f, owner); var observed = await store.ObserveTenantExperienceSourceAsync(new(owner.Tenant, current.Frame.RunId), current.Frame.Generation, ExperienceSourceKind.Session, source.Frame.SessionId);
        await SaveExperienceSource(f, current, observed); await CommitExperience(f, current, observed); owner = await ExperienceSettings(f, owner, false);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.RequireTenantExperienceSourcesAsync(new(owner.Tenant, current.Frame.RunId), [new(owner.Tenant, observed.Witness)], [], current.Frame.Generation).AsTask());
        Assert.Single((await ((ITenantExperienceManagementStore)f.Iam).ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new())).Items);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_source_proofs_survive_compaction_and_prevent_changed_source_final_output(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source)); var current = await MemoryClaim(f, owner);
        var store = (ITenantExperienceStore)f.Iam; var observed = await store.ObserveTenantExperienceSourceAsync(new(owner.Tenant, current.Frame.RunId), current.Frame.Generation, ExperienceSourceKind.Session, source.Frame.SessionId);
        var saved = await SaveExperienceSource(f, current, observed); var proofs = TenantExperienceReferences.Sources(saved.Checkpoint); Assert.Single(proofs);
        Task<AgentRun> Save(AgentRunCheckpoint checkpoint) => ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(saved.Owner, saved.AgentRunId,
            new AgentRunCommand.Checkpoint(saved.Revision, DateTimeOffset.UtcNow, current.Frame.Generation, checkpoint, null), f.Ids.NewId()).AsTask();
        await Assert.ThrowsAsync<AccessDeniedException>(() => Save(new(AgentRunToolCallCheckpoint.Write([]), 1, 0, 180000, 0)));
        var compact = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([], executionState: new(TenantExperienceSourceReads: proofs)), 1, 0, 180000, 0);
        saved = await Save(compact); Assert.Equal(proofs, TenantExperienceReferences.Sources(saved.Checkpoint)); var plan = await MemoryPlan(f, current);
        await ChangeHistory(f, owner, source.Frame.SessionId); await Assert.ThrowsAsync<AccessDeniedException>(() => Save(compact));
        await Assert.ThrowsAsync<AccessDeniedException>(() => MemoryCommit(f, current, plan));
    }
    [Fact]
    public async Task Tenant_experience_SQLite_upgrade_freezes_owner_source_content_and_consent_and_refuses_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true); var owner = await ExperienceOwner(f); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source)); var current = await MemoryClaim(f, owner);
        await using var db = f.Contexts!.CreateDbContext(); var migration = db.GetService<IMigrator>(); await migration.MigrateAsync("20261021201045_TenantContinuityRead");
        var previous = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger'").ToArrayAsync();
        await migration.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges()); var latest = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger'").ToArrayAsync(); Assert.All(previous, name => Assert.Contains(name, latest));
        var store = (ITenantExperienceStore)f.Iam; var observed = await store.ObserveTenantExperienceSourceAsync(new(owner.Tenant, current.Frame.RunId), current.Frame.Generation, ExperienceSourceKind.Session, source.Frame.SessionId);
        await SaveExperienceSource(f, current, observed); var fact = (await CommitExperience(f, current, observed)).Observation.Value.ResultExperienceId;
        foreach (var sql in new[] { "DELETE FROM TenantExperiences", "UPDATE TenantExperiences SET OwnerKey='instance', UserPrincipalId=NULL, AccountId=NULL, UserId=NULL", "UPDATE TenantExperiences SET SourceId='00000000-0000-0000-0000-000000000000', Revision=Revision+1", "UPDATE TenantExperiences SET ContentJson='{{}}', Revision=Revision+1", "UPDATE TenantExperiences SET Visibility=2, Revision=Revision+1" })
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => migration.MigrateAsync("20261021201045_TenantContinuityRead")); await migration.MigrateAsync();
        Assert.True(await db.Set<TenantExperienceRecord>().AnyAsync(i => i.ExperienceId == fact));
        var reviewTurn = await MemoryClaim(f, owner); await MemoryPolicy(f, owner, "sessions.read", OperationApprovalMode.Always); var s = ExperienceServices(f);
        var request = await PrepareExperienceReview(f, reviewTurn.Frame, new("source", TenantExperienceToolContract.Source, JsonSerializer.Serialize(new { sourceKind = "Session", sourceId = source.Frame.SessionId })), s.Approvals);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.ExperienceOperation.Source.ThroughCursor',999) WHERE RequestId={request.RequestId}"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => migration.MigrateAsync("20261021212948_TenantExperience")); await migration.MigrateAsync();
        var reopened = ExperienceServices(f, new SqliteIamStore(f.Contexts, f.Content)); Assert.True((await reopened.Approvals.ReadViewAsync(owner.Tenant, request.RequestId, new(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId))).Review!.CanDecide);
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT 'violation' AS Value FROM pragma_foreign_key_check").ToArrayAsync());
    }
}
