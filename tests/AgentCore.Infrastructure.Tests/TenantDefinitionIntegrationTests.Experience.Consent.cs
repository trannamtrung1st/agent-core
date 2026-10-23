using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    private static (TenantExperienceOperationService Experience, TenantContinuityReadService Reads, TenantApprovalService Approvals, TenantApprovedOperationService Effects, TenantRunContinuationService Continuation) ExperienceServices(Fixture f, IIamStore? current = null)
    {
        current ??= f.Iam; var iam = new IamAuthorizer(current); var runs = (ITenantRunStore)current;
        var operations = new TenantOperationAuthorizer(current, runs, (ITenantAgentStore)current, iam, new((ITenantAuthorizationAuditStore)current, f.Ids, TimeProvider.System));
        var builtIns = new SessionNoBuiltinFallback(); var configuration = new TenantRunConfigurationResolver((ITenantAgentStore)current, f.Definitions, builtIns,
            new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)current);
        var experience = new TenantExperienceOperationService((ITenantExperienceStore)current, operations, iam, (ITenantApprovalStore)current, f.Ids, TimeProvider.System);
        var reads = new TenantContinuityReadService((ITenantContinuityStore)current, (ITenantLearnedMemoryStore)current, operations, iam, TimeProvider.System);
        var approvals = new TenantApprovalService(current, (ITenantApprovalStore)current, (ITenantAgentStore)current, operations, iam, f.Identity, f.Ids, TimeProvider.System,
            configuration, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)current, experienceOperations: experience, continuityReads: reads);
        var effects = new TenantApprovedOperationService(current, runs, (ITenantAgentStore)current, (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current, operations,
            configuration, f.Ids, TimeProvider.System, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, experienceOperations: experience, continuityReads: reads);
        return (experience, reads, approvals, effects, new(runs, (ITenantApprovalStore)current, effects, TimeProvider.System, (ITenantRuntimeStore)current, f.Ids));
    }
    private static async Task<TenantApprovalRequest> PrepareExperienceReview(Fixture f, TenantRuntimeFrame frame, ModelToolCall call, TenantApprovalService approvals)
    {
        var authorization = new ModelToolCall("experience-consent", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = TenantExperienceToolContract.OperationName(call.Name),
            experience = new { tool = call.Name, arguments = JsonDocument.Parse(call.ArgumentsJson).RootElement } }));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(frame.Owner.TenantId!.Value, frame.RunId))!;
        var state = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint) with {
            TenantExperienceSourceReads = TenantExperienceReferences.Sources(run.Checkpoint), TenantExperienceSources = TenantExperienceReferences.Facts(run.Checkpoint) };
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, DateTimeOffset.UtcNow, frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [authorization])], executionState: state), 2, 0, 180000, 0), null), f.Ids.NewId());
        var result = await approvals.PrepareExperienceAsync(new(frame.Owner.TenantId!.Value, frame.RunId), call, run.Revision, frame.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, result.Decision); return Assert.IsType<TenantApprovalRequest>(result.Request);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_source_Review_withholds_then_resumes_exact_observation_and_records_under_independent_manage(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); await MemoryPolicy(f, owner, "sessions.read", OperationApprovalMode.Always); var service = ExperienceServices(f);
        var call = new ModelToolCall("source", TenantExperienceToolContract.Source, JsonSerializer.Serialize(new { sourceKind = "Session", sourceId = source.Frame.SessionId }));
        var before = await service.Experience.ExecuteAsync(current.Frame, call, 6000); Assert.Contains("requiresApproval", before); Assert.DoesNotContain("Evidence response completed", before);
        var request = await PrepareExperienceReview(f, current.Frame, call, service.Approvals); Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(owner.Tenant, request.RequestId));
        var view = await service.Approvals.ReadViewAsync(owner.Tenant, request.RequestId, new(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId)); Assert.True(view.Review!.CanDecide, view.Review.DecisionUnavailableReason);
        Assert.Contains(view.Review.Changes, i => i.Field == "Exact Experience sources" && i.After!.ToString()!.Contains("Evidence response completed"));
        var frame = await DecideMemoryReview(f, owner, request, service.Approvals); if (sqlite) service = ExperienceServices(f, new SqliteIamStore(f.Contexts!, f.Content));
        var resumed = await service.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, frame.RunId))!); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        var witness = Assert.Single(TenantExperienceReferences.Sources(resumed.Run.Checkpoint)).Witness;
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var transcript)); Assert.Contains("Evidence response completed", Assert.Single(transcript!, m => m.Role == ModelRole.Tool).Text);
        Assert.Contains("tenantExperienceSourceRead", await service.Experience.ExecuteAsync(frame, call, 6000));
        var record = new ModelToolCall("record", TenantExperienceToolContract.Record, ExperienceRecordArguments(witness));
        var body = await service.Experience.ExecuteAsync(frame, record, 6000); Assert.Contains("\"applied\":true", body);
        var fact = Assert.Single((await ((ITenantExperienceManagementStore)f.Iam).ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new())).Items).Experience; Assert.Equal(owner.Human.PrincipalId, fact.UserPrincipalId);
        Assert.Contains("\"alreadyCompleted\":true", await service.Experience.ExecuteAsync(frame, record, 6000));
        var other = await MemoryClaim(f, owner); Assert.Contains("requiresApproval", await service.Experience.ExecuteAsync(other.Frame, call, 6000));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_continuity_returns_owned_facts_and_exact_fact_Review_without_using_Memory_enablement(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); var store = (ITenantExperienceStore)f.Iam; var observed = await store.ObserveTenantExperienceSourceAsync(new(owner.Tenant, current.Frame.RunId), current.Frame.Generation, AgentCore.Domain.Experience.ExperienceSourceKind.Session, source.Frame.SessionId);
        await SaveExperienceSource(f, current, observed); _ = await CommitExperience(f, current, observed); var fact = Assert.Single((await store.ReadTenantRunExperienceAsync(current.Frame)).Items);
        var services = ExperienceServices(f); var call = new ModelToolCall("get", ToolCatalog.ContinuityGet, JsonSerializer.Serialize(new { kind = "Experience", id = fact.ExperienceId }));
        var ordinary = await services.Reads.InspectAsync(current.Frame, call, 6000); Assert.Contains("Keep evidence grouping explicit", ordinary); Assert.Single(TenantExperienceReferences.Facts([new(ModelRole.Tool, ordinary, ToolCallId: "get", Name: ToolCatalog.ContinuityGet)]));
        await MemoryPolicy(f, owner, "memory.read", OperationApprovalMode.Always); Assert.Contains("requiresApproval", await services.Reads.InspectAsync(current.Frame, call, 6000));
        var request = await PrepareContinuityReview(f, current.Frame, call, services.Approvals); Assert.NotNull(request.Operation.ContinuityRead!.Experience); Assert.Null(request.Operation.ContinuityRead.Memory);
        var frame = await DecideMemoryReview(f, owner, request, services.Approvals); if (sqlite) services = ExperienceServices(f, new SqliteIamStore(f.Contexts!, f.Content));
        var resumed = await services.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, frame.RunId))!); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        Assert.Single(TenantExperienceReferences.Facts(resumed.Run.Checkpoint)); Assert.Contains("Keep evidence grouping explicit", await services.Reads.InspectAsync(frame, call, 6000));
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_experience_record_requires_exact_manage_Review_or_primary_only_grant(bool sqlite, bool exceptional)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); var store = (ITenantExperienceStore)f.Iam; var observed = await store.ObserveTenantExperienceSourceAsync(new(owner.Tenant, current.Frame.RunId), current.Frame.Generation, AgentCore.Domain.Experience.ExperienceSourceKind.Session, source.Frame.SessionId);
        await SaveExperienceSource(f, current, observed);
        if (exceptional) await MemoryPermissions(f, owner, "agents.chat", "agents.read", "sessions.read", "memory.read");
        else await MemoryPolicy(f, owner, "memory.manage", OperationApprovalMode.Always);
        var services = ExperienceServices(f); var call = new ModelToolCall("record", TenantExperienceToolContract.Record, ExperienceRecordArguments(observed.Witness));
        Assert.Contains("requiresApproval", await services.Experience.ExecuteAsync(current.Frame, call, 6000)); Assert.Empty((await store.ReadTenantRunExperienceAsync(current.Frame)).Items);
        var request = await PrepareExperienceReview(f, current.Frame, call, services.Approvals); Assert.Equal(exceptional ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
        var frame = await DecideMemoryReview(f, owner, request, services.Approvals); if (sqlite) services = ExperienceServices(f, new SqliteIamStore(f.Contexts!, f.Content));
        var resumed = await services.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, frame.RunId))!); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        var fact = Assert.Single((await store.ReadTenantRunExperienceAsync(frame)).Items); Assert.Equal(request.Operation.ExperienceOperation!.ResultExperienceId, fact.ExperienceId);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var transcript)); var output = Assert.Single(transcript!, m => m.Role == ModelRole.Tool).Text; Assert.Contains("\"applied\":true", output); Assert.Contains(fact.ExperienceId.ToString("D"), output);
        if (exceptional) Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(owner.Tenant, request.RequestId))!.Status);
        Assert.Equal(resumed.Outcome.OperationId, (await services.Effects.ExecuteAsync(new(owner.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash)).OperationId);
        Assert.Single((await store.ReadTenantRunExperienceAsync(frame)).Items);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_consolidation_keeps_lineage_and_only_its_task_can_finish_from_its_known_supersession(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceSettings(f, await ExperienceOwner(f), true, true);
        var source1 = await MemoryClaim(f, owner); await MemoryCommit(f, source1, await MemoryPlan(f, source1));
        var source2 = await MemoryClaim(f, owner); await MemoryCommit(f, source2, await MemoryPlan(f, source2));
        var current = await MemoryClaim(f, owner); var store = (ITenantExperienceStore)f.Iam;
        foreach (var source in new[] { source1, source2 })
        {
            var observed = await store.ObserveTenantExperienceSourceAsync(new(owner.Tenant, current.Frame.RunId), current.Frame.Generation, AgentCore.Domain.Experience.ExperienceSourceKind.Session, source.Frame.SessionId);
            await SaveExperienceSource(f, current, observed); _ = await CommitExperience(f, current, observed, source.Frame.SessionId.ToString("D"));
        }
        var facts = (await store.ReadTenantRunExperienceAsync(current.Frame)).Items; Assert.Equal(2, facts.Count);
        var proofs = facts.Select(i => new TenantExperienceFactSource(i.TenantId, i.ExperienceId, i.ContentHash, i.Revision)).ToArray();
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, current.Frame.RunId))!;
        var state = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint) with { TenantExperienceSourceReads = TenantExperienceReferences.Sources(run.Checkpoint), TenantExperienceSources = proofs };
        _ = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, DateTimeOffset.UtcNow, current.Frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([], executionState: state), 2, 0, 180000, 0), null), f.Ids.NewId());
        var body = JsonSerializer.SerializeToNode(new AgentCore.Domain.Experience.ExperienceContent("Consolidate observed evidence grouping", ["Compared two retained observations"], [], ["Kept one observation"], [], [], [], ["Keep evidence grouping explicit"]), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        body["sourceExperienceIds"] = JsonSerializer.SerializeToNode(facts.Select(i => i.ExperienceId).ToArray());
        var services = ExperienceServices(f); var call = new ModelToolCall("consolidate", TenantExperienceToolContract.Consolidate, body.ToJsonString());
        Assert.Contains("requiresApproval", await services.Experience.ExecuteAsync(current.Frame, call, 6000));
        var request = await PrepareExperienceReview(f, current.Frame, call, services.Approvals); var frame = await DecideMemoryReview(f, owner, request, services.Approvals);
        if (sqlite) services = ExperienceServices(f, new SqliteIamStore(f.Contexts!, f.Content));
        var resumed = await services.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, frame.RunId))!); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        var replacement = Assert.Single((await store.ReadTenantRunExperienceAsync(frame)).Items); Assert.Equal(2, replacement.Parents.Count); Assert.All(facts, i => Assert.Contains(i.ExperienceId, replacement.Parents));
        await store.RequireTenantExperienceSourcesAsync(new(owner.Tenant, frame.RunId), [], proofs, frame.Generation);
        var other = await MemoryClaim(f, owner); await Assert.ThrowsAsync<AccessDeniedException>(() => store.RequireTenantExperienceSourcesAsync(new(owner.Tenant, other.Frame.RunId), [], proofs, other.Frame.Generation).AsTask());
        var management = (ITenantExperienceManagementStore)f.Iam; var page = await management.ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(IncludeHistorical: true));
        Assert.Equal(2, page.Items.Count(i => i.Experience.Visibility == AgentCore.Domain.Experience.ExperienceVisibility.Superseded));
        Assert.NotNull(await management.TryChangeOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(page.SecurityRevision, replacement.ExperienceId, replacement.Revision),
            new(f.Ids.NewId(), owner.Tenant, owner.Human.PrincipalId, "experience.manage", owner.Agent.Instance.InstanceId.ToString("D"), "allowed", "human_experience_changed", DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.RequireTenantExperienceSourcesAsync(new(owner.Tenant, frame.RunId), [], proofs, frame.Generation).AsTask());
    }

}
