using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    private static async Task MemoryPolicy(Fixture f, MemorySetup s, string key, OperationApprovalMode mode, bool independent = false) => await new TenantOperationPolicyService(f.Iam,
        (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System).SaveAsync(s.Tenant, new TenantSessionInitiator(s.Human.AccountId, s.Human.AuthenticationSessionId, s.Human.PrincipalId), key,
        new(mode, independent), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
    private static async Task MemoryPermissions(Fixture f, MemorySetup s, params string[] permissions) => await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, s.Role,
        "Independent learned-memory rights", permissions, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_memory_operation_read_Review_withholds_values_until_exact_durable_consent_then_reuses_only_same_task_sources(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await MemoryOperationFixture(f); await MemoryPolicy(f, s.Owner, "memory.read", OperationApprovalMode.Always); var services = MemoryServices(f);
        var call = new ModelToolCall("inspect", ToolCatalog.ContinuitySearch, "{\"query\":\"Evidence\",\"limit\":10}");
        var before = await services.Memory.InspectAsync(s.Turn.Frame, call, 6000); Assert.Contains("requiresApproval", before); Assert.DoesNotContain("grouped", before);
        var request = await PrepareMemoryReview(f, s.Turn.Frame, call, services.Approvals); Assert.Equal(ApprovalKind.Review, request.Kind);
        var frame = await DecideMemoryReview(f, s.Owner, request, services.Approvals); if (sqlite) services = MemoryServices(f, new SqliteIamStore(f.Contexts!, f.Content));
        var resumed = await services.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Owner.Tenant, frame.RunId))!); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages)); var output = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        Assert.Contains("Prefer complete grouped evidence", output); using var data = JsonDocument.Parse(output); Assert.True(data.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.False(data.RootElement.GetProperty("applied").GetBoolean());
        var references = TenantMemoryOperationReferences.Read(resumed.Run.Checkpoint); Assert.NotEmpty(references);
        await ((ITenantLearnedMemoryStore)f.Iam).RequireTenantMemorySourcesAsync(new(s.Owner.Tenant, frame.RunId), references, frame.Generation);
        var get = new ModelToolCall("get", ToolCatalog.ContinuityGet, JsonSerializer.Serialize(new { kind = "Memory", id = s.Sources[0].MemoryId }));
        Assert.Contains(s.Sources[0].Content, await services.Memory.InspectAsync(frame, get, 6000));
        var later = await MemoryClaim(f, s.Owner); Assert.DoesNotContain(s.Sources[0].Content, await services.Memory.InspectAsync(later.Frame, get, 6000));
        await MemoryPermissions(f, s.Owner, "agents.chat", "agents.read", "sessions.read", "memory.manage");
        await Assert.ThrowsAsync<AccessDeniedException>(() => services.Memory.InspectAsync(frame, get, 6000).AsTask());
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_memory_operation_forget_requires_exact_Review_or_primary_only_grant_and_cannot_relearn_in_its_successor(bool sqlite, bool exceptional)
    {
        await using var f = await Fixture.Create(sqlite); var s = await MemoryOperationFixture(f); var services = MemoryServices(f);
        if (exceptional) await MemoryPermissions(f, s.Owner, "agents.chat", "agents.read", "sessions.read", "memory.read");
        var source = s.Sources[0]; var call = new ModelToolCall("forget", ToolCatalog.MemoryForget, JsonSerializer.Serialize(new { memoryId = source.MemoryId }));
        var request = await PrepareMemoryReview(f, s.Turn.Frame, call, services.Approvals); Assert.Equal(exceptional ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
        var frame = await DecideMemoryReview(f, s.Owner, request, services.Approvals);
        var resumed = await services.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Owner.Tenant, frame.RunId))!); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        await ((ITenantLearnedMemoryStore)f.Iam).RequireTenantMemorySourcesAsync(new(s.Owner.Tenant, frame.RunId), [new(source.TenantId, source.MemoryId, source.ContentHash)], frame.Generation);
        var tombstone = Assert.Single((await services.Memory.PrepareAsync(new(s.Owner.Tenant, frame.RunId), frame.Generation, ToolCatalog.ContinuityGet, JsonSerializer.Serialize(new { kind = "Memory", id = source.MemoryId }))).Items);
        Assert.Equal(MemoryItemStatus.Deleted, tombstone.Status); Assert.Empty(tombstone.Content); Assert.Empty(tombstone.Subject);
        // Exact maintenance does not revoke independent User/Session copies, and a completing
        // model cannot silently recreate or promote its just-forgotten fact in this task.
        var snapshot = (await ((ITenantRuntimeStore)f.Iam).LoadTenantRuntimeAsync(frame))!;
        var plan = await ((ITenantLearnedMemoryStore)f.Iam).PrepareTenantLearningAsync(frame, snapshot, [Learned(source.Content)],
            Enumerable.Range(0, TenantMemoryRules.CandidateCount).Select(_ => f.Ids.NewId()).ToArray(), f.Ids.NewId(), DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.Empty(plan.Changes);
        var page = await HumanMemories(f).ReadAsync(s.Owner.Tenant, s.Owner.Agent.Instance.InstanceId, s.Owner.Human, new(MemoryScope.User)); Assert.Equal(2, page.RetainedCount);
        if (exceptional) Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Owner.Tenant, request.RequestId))!.Status);
    }
    [Theory] [InlineData(false, "source")] [InlineData(true, "source")] [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    public async Task Tenant_memory_operation_changed_source_revoked_read_or_refusal_never_partially_consolidates(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var s = await MemoryOperationFixture(f); var services = MemoryServices(f);
        var request = await PrepareMemoryReview(f, s.Turn.Frame, Consolidate(s.Sources), services.Approvals);
        var frame = await DecideMemoryReview(f, s.Owner, request, services.Approvals, boundary != "reject");
        if (boundary == "source")
        {
            var query = new TenantMemoryQuery(MemoryScope.IdentityUser); var page = await HumanMemories(f).ReadAsync(s.Owner.Tenant, s.Owner.Agent.Instance.InstanceId, s.Owner.Human, query);
            await HumanMemories(f).ForgetAsync(s.Owner.Tenant, s.Owner.Agent.Instance.InstanceId, s.Owner.Human, new(query, page.SecurityRevision, s.Sources[0].MemoryId, s.Sources[0].Revision));
        }
        if (boundary == "read") await MemoryPermissions(f, s.Owner, "agents.chat", "agents.read", "sessions.read", "memory.manage");
        if (boundary == "read")
        {
            var receipt = await services.Effects.ExecuteAsync(new(s.Owner.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash);
            Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome); Assert.Equal("memory_read_required", receipt.ReasonCode);
        }
        else
        {
            var resumed = await services.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Owner.Tenant, frame.RunId))!);
            Assert.NotEqual(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        }
        var current = await HumanMemories(f).ReadAsync(s.Owner.Tenant, s.Owner.Agent.Instance.InstanceId, s.Owner.Human, new(MemoryScope.IdentityUser));
        Assert.DoesNotContain(current.Items, i => i.Memory.Subject == "Evidence practice"); Assert.Equal(MemoryItemStatus.Active, current.Items.Single(i => i.Memory.MemoryId == s.Sources[1].MemoryId).Memory.Status);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_memory_operation_independent_reviewer_cannot_adopt_private_User_facts_and_raw_read_cannot_be_granted(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await MemoryOperationFixture(f); await MemoryPolicy(f, s.Owner, "memory.manage", OperationApprovalMode.Always, true); var services = MemoryServices(f);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Owner.Tenant, s.Turn.Frame.RunId))!;
        var denied = await services.Approvals.PrepareMemoryAsync(new(s.Owner.Tenant, run.AgentRunId), Consolidate(s.Sources), run.Revision, s.Turn.Frame.Generation);
        Assert.Equal(OperationDecisionKind.Denied, denied.Decision); Assert.Equal("no_eligible_approver", denied.ReasonCode); Assert.Null(denied.Request);
        await MemoryPermissions(f, s.Owner, "agents.chat", "sessions.read", "agents.read", "memory.manage");
        await Assert.ThrowsAsync<AccessDeniedException>(() => services.Memory.PrepareAsync(new(s.Owner.Tenant, run.AgentRunId), s.Turn.Frame.Generation, ToolCatalog.ContinuitySearch, "{\"query\":\"\"}").AsTask());
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Owner.Tenant, denied.Operation.OperationId));
    }
}
