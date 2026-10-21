using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    private static async Task<MemorySetup> ExperienceOwner(Fixture f, bool enabled = true)
    {
        var owner = await MemoryOwner(f, operations: true, memoryEnabled: false);
        return await ExperienceSettings(f, owner, enabled);
    }
    private static async Task<MemorySetup> ExperienceSettings(Fixture f, MemorySetup owner, bool enabled, bool maintenance = false)
    {
        var agents = (ITenantAgentStore)f.Iam; var snapshot = (await agents.ReadTenantAgentConfigurationAsync(owner.Tenant, owner.Agent.Instance.InstanceId))!;
        var state = (await f.Iam.ReadTenantAsync(owner.Tenant))!;
        var changed = await agents.TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            new(snapshot.Agent.Instance.InstanceId, snapshot.Agent.Instance.Revision, SetSettingsOverrides: true, SettingsOverrides: snapshot.Agent.Instance.SettingsOverrides,
                AutomationPolicyChanges: [], Initiator: owner.Human, Isolation: new(new(snapshot.Agent.Instance.Revision, snapshot.Isolation.ExperienceRevision, snapshot.Isolation.MaintenanceRevision), enabled, maintenance)),
            DateTimeOffset.UtcNow, new(f.Ids.NewId(), owner.Tenant, owner.Human.PrincipalId, "agents.state-isolation.update", snapshot.Agent.Instance.InstanceId.ToString("D"), "allowed", "test", DateTimeOffset.UtcNow));
        return owner with { Agent = Assert.IsType<TenantAgent>(changed) };
    }
    private static string ExperienceRecordArguments(TenantExperienceSourceWitness witness, string goal = "Keep evidence grouping explicit")
    {
        var body = JsonSerializer.SerializeToNode(new ExperienceContent(goal, ["Inspected retained conversation"], ["Use bounded evidence"], ["Confirmed observed result"], [], [], [], ["Check source provenance"]), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        body["sourceKind"] = witness.Kind.ToString(); body["sourceId"] = witness.SourceId.ToString("D"); body["throughCursor"] = witness.ThroughCursor; return body.ToJsonString();
    }
    private static async Task<AgentRun> SaveExperienceSource(Fixture f, MemoryTurn turn, TenantExperienceSourceObservation source)
    {
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(turn.Frame.Owner.TenantId!.Value, turn.Frame.RunId))!;
        var text = JsonSerializer.Serialize(new { tenantExperienceSourceRead = true, sourceWitness = new TenantExperienceSourceReference(source.Reader.Security.Tenant.TenantId, source.Witness), evidence = JsonDocument.Parse(source.EvidenceJson).RootElement });
        var state = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint) with { TenantExperienceSourceReads = TenantExperienceReferences.Sources(run.Checkpoint), TenantExperienceSources = TenantExperienceReferences.Facts(run.Checkpoint) };
        return await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, DateTimeOffset.UtcNow, turn.Frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, text, ToolCallId: "inspect", Name: TenantExperienceToolContract.Source)], executionState: state), 1, 0, 180000, 0), null), f.Ids.NewId());
    }
    private static async Task<(TenantOperationReceipt? Receipt, TenantExperienceOperationObservation Observation)> CommitExperience(Fixture f, MemoryTurn current, TenantExperienceSourceObservation source, string call = "record", string goal = "Keep evidence grouping explicit", long? securityRevision = null)
    {
        var store = (ITenantExperienceStore)f.Iam;
        var observed = await store.ObserveTenantExperienceOperationAsync(new(source.Reader.Security.Tenant.TenantId, current.Frame.RunId), current.Frame.Generation,
            TenantExperienceToolContract.Parse(TenantExperienceToolContract.Record, ExperienceRecordArguments(source.Witness, goal)));
        var state = (await f.Iam.ReadTenantAsync(observed.Reader.Security.Tenant.TenantId))!; var run = observed.Reader.Run;
        var prepared = TenantApprovalRules.PrepareExperience(f.Ids.NewId(), run.AgentInstanceId, observed.Value, observed.Reader.Agent.Instance.Revision, observed.Reader.Agent.Instance.PersonaRevision);
        var commit = new TenantExperienceCommit(prepared.OperationId, run.AgentRunId, current.Frame.Generation, run.Revision, call,
            TenantMemoryOperationRules.Hash(observed.Arguments.CanonicalArguments), observed.Value, observed.Reader.Agent.Instance.Revision, prepared.ActionHash);
        var audit = new AuthorizationAudit(f.Ids.NewId(), state.Tenant.TenantId, observed.Reader.Agent.PrincipalId, "operations.experience.record", run.AgentInstanceId.ToString("D"), "allowed", TenantExperienceRules.Recorded, DateTimeOffset.UtcNow);
        var receipt = await store.TryCommitTenantExperienceAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, securityRevision ?? state.Tenant.Revision, commit, audit);
        return (receipt, observed);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_inspects_records_recalls_and_reopens_without_global_Profile_or_Memory(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); var store = (ITenantExperienceStore)f.Iam;
        var observed = await store.ObserveTenantExperienceSourceAsync(new(owner.Tenant, current.Frame.RunId), current.Frame.Generation, ExperienceSourceKind.Session, source.Frame.SessionId);
        Assert.Contains("Evidence response completed.", observed.EvidenceJson); Assert.True(System.Text.Encoding.UTF8.GetByteCount(observed.EvidenceJson) <= TenantExperienceSourceProjection.EvidenceBytes);
        await Assert.ThrowsAsync<AccessDeniedException>(() => CommitExperience(f, current, observed));
        await SaveExperienceSource(f, current, observed); var recorded = await CommitExperience(f, current, observed); Assert.Equal(TenantExperienceRules.Recorded, recorded.Receipt!.ReasonCode);
        var retry = await CommitExperience(f, current, observed); Assert.Equal(recorded.Receipt.OperationId, retry.Receipt!.OperationId);
        var read = await store.ReadTenantRunExperienceAsync(current.Frame); var item = Assert.Single(read.Items);
        Assert.Equal(owner.Human.PrincipalId, item.UserPrincipalId); Assert.Equal(owner.Human.AccountId, item.AccountId); Assert.Equal(current.Run.AgentRunId, item.RunId); Assert.Equal(source.Frame.SessionId, item.SourceId);
        Assert.Equal(recorded.Observation.Value.ResultExperienceId, item.ExperienceId); Assert.Equal("Keep evidence grouping explicit", item.Content!.Goal);
        Assert.Empty((await ((ITenantLearnedMemoryStore)f.Iam).ReadTenantRunMemoryAsync(current.Frame)).Items);
        var facts = new[] { new TenantExperienceFactSource(owner.Tenant, item.ExperienceId, item.ContentHash, item.Revision) };
        await store.RequireTenantExperienceSourcesAsync(new(owner.Tenant, current.Frame.RunId), [], facts, current.Frame.Generation);
        if (sqlite)
        {
            store = new SqliteIamStore(f.Contexts!, f.Content); Assert.Equal(item, Assert.Single((await store.ReadTenantRunExperienceAsync(current.Frame)).Items) with { Content = item.Content });
            await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Profiles.ToArrayAsync()); Assert.Empty(await db.StructuredMemories.ToArrayAsync());
            Assert.Single(await db.Set<TenantExperienceRecord>().ToArrayAsync()); Assert.Single(await db.Set<TenantOperationReceiptRecord>().Where(i => i.ReasonCode == TenantExperienceRules.Recorded).ToArrayAsync());
        }
        Assert.Single(await f.Iam.ReadAuditAsync(owner.Tenant, 200), i => i.Action == "operations.experience.record");
        Assert.DoesNotContain("Check source provenance", JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(owner.Tenant, 200)));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_human_suppression_and_tombstone_are_current_CAS_and_model_opt_out_independent(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); var store = (ITenantExperienceStore)f.Iam;
        var observed = await store.ObserveTenantExperienceSourceAsync(new(owner.Tenant, current.Frame.RunId), current.Frame.Generation, ExperienceSourceKind.Session, source.Frame.SessionId);
        await SaveExperienceSource(f, current, observed); _ = await CommitExperience(f, current, observed);
        var management = (ITenantExperienceManagementStore)f.Iam; var page = await management.ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new()); var item = Assert.Single(page.Items).Experience;
        AuthorizationAudit Audit() => new(f.Ids.NewId(), owner.Tenant, owner.Human.PrincipalId, "experience.manage", owner.Agent.Instance.InstanceId.ToString("D"), "allowed", "human_experience_changed", DateTimeOffset.UtcNow);
        var suppress = new TenantExperienceChange(page.SecurityRevision, item.ExperienceId, item.Revision, ExperienceVisibility.Suppressed);
        Assert.Null(await management.TryChangeOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, suppress with { ExpectedExperienceRevision = item.Revision + 1 }, Audit()));
        Assert.NotNull(await management.TryChangeOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, suppress, Audit()));
        Assert.Empty((await store.ReadTenantRunExperienceAsync(current.Frame)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.RequireTenantExperienceSourcesAsync(new(owner.Tenant, current.Frame.RunId), [], [new(owner.Tenant, item.ExperienceId, item.ContentHash, item.Revision)], current.Frame.Generation).AsTask());
        owner = await ExperienceSettings(f, owner, false);
        page = await management.ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new()); Assert.Equal(ExperienceVisibility.Suppressed, Assert.Single(page.Items).Experience.Visibility);
        Assert.NotNull(await management.TryChangeOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(page.SecurityRevision, ExpectedRetainedCount: page.RetainedCount), Audit()));
        page = await management.ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(IncludeHistorical: true)); var deleted = Assert.Single(page.Items).Experience;
        Assert.Null(deleted.Content); Assert.Equal(item.ContentHash, deleted.ContentHash); Assert.Equal(ExperienceVisibility.Deleted, deleted.Visibility); Assert.Equal(item.SourceWitnessJson, deleted.SourceWitnessJson); Assert.Equal(0, page.RetainedCount);
        Assert.Null(await management.TryChangeOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(page.SecurityRevision, deleted.ExperienceId, deleted.Revision, ExperienceVisibility.Eligible), Audit()));
        if (sqlite) { management = new SqliteIamStore(f.Contexts!, f.Content); Assert.Null(Assert.Single((await management.ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(IncludeHistorical: true))).Items).Experience.Content); }
    }
}
