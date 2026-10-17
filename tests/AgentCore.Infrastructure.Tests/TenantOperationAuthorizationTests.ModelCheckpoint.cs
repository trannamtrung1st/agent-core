using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Durable_model_approval_wait_preserves_active_budget_across_a_long_human_delay(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f); var storage = (ITenantRuntimeStore)f.Iam;
        var run = await storage.ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
            new AgentRunCommand.Claim(1, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var persona = setup.Agent.Instance.Persona with { Name = "Budget-preserved proposal" };
        var call = new ModelToolCall("authorization", ToolCatalog.RequestAuthorization,
            JsonSerializer.Serialize(new { operation = "update_persona", persona }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var payload = AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]);
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, clock.GetUtcNow(), run.Claim!.Generation, new(payload, 1, 0, 10000, 100), null), f.Ids.NewId());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var approval = ApprovalService(f);
        var request = (await approval.PreparePersonaAsync(new(setup.Tenant, run.AgentRunId), persona, run.Revision, run.Claim!.Generation)).Request!;
        var waiting = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
        Assert.Null(waiting.Checkpoint!.BudgetRecordedAtUtc); Assert.Equal(9500, waiting.Checkpoint.RemainingOverallBudgetMs); Assert.Equal(600, waiting.Checkpoint.ActiveExecutionMs);
        clock.Advance(TimeSpan.FromMinutes(20));
        await approval.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, request.Revision);
        await approval.DispatchPendingAsync();
        var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
        Assert.Equal(AgentRunOutcomeKind.Continued, queued.Result!.OutcomeKind);
        queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, queued.Result.ContinuationRunId!.Value))!;
        Assert.Equal(run.AttemptCount, queued.AttemptCount); Assert.Equal(run.ResponseId, queued.ResponseId);
        var claimed = await storage.ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var resumed = await Continuations(f).ResumeAsync(claimed);
        Assert.Equal(9500, resumed.Run.Checkpoint!.RemainingOverallBudgetMs); Assert.Equal(600, resumed.Run.Checkpoint.ActiveExecutionMs);
        Assert.Equal(TenantOperationOutcome.Denied, resumed.Outcome!.Outcome); // The disabled pinned Settings policy still wins.
        Assert.Equal("management_unavailable", resumed.Outcome.ReasonCode);
    }

    [Theory]
    [InlineData(false, "valid")] [InlineData(true, "valid")]
    [InlineData(false, "args")] [InlineData(true, "args")]
    [InlineData(false, "batch")] [InlineData(true, "batch")]
    public async Task Pending_model_authorization_checkpoint_must_match_saved_persona_before_effect_and_closes_with_a_durable_result(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await EffectSetup(f);
        var persona = boundary == "args" ? setup.Request.Operation.Persona! with { Name = "Different model proposal" } : setup.Request.Operation.Persona;
        var call = new ModelToolCall("authorization", ToolCatalog.RequestAuthorization,
            JsonSerializer.Serialize(new { operation = "update_persona", persona }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        ModelToolCall[] batch = boundary == "batch" ? [call, new("other", ToolCatalog.IamEffectivePermissions, "{}")] : [call];
        var payload = AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: batch)]);
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(setup.Run.Revision, clock.GetUtcNow(), setup.Generation, new(payload, batch.Length, 0, 10000, 100), null), f.Ids.NewId());
        if (boundary != "valid")
        {
            await Assert.ThrowsAsync<SecurityConflictException>(() => Continuations(f).ResumeAsync(run).AsTask());
            Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Request.TenantId, setup.Request.Operation.OperationId));
            Assert.Equal(OperationGrantStatus.Available, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Request.TenantId, setup.Request.RequestId))!.Status);
            Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Request.TenantId, 200), a => a.Action == "operations.persona.apply");
            return;
        }
        var resumed = await Continuations(f).ResumeAsync(run);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages)); Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(messages!));
        Assert.Contains(messages!, m => m.Role == ModelRole.Tool && m.ToolCallId == call.Id && m.Text.Contains("operation_completed"));
        var replay = await Continuations(f).ResumeAsync(resumed.Run);
        Assert.Equal(resumed.Run.Revision, replay.Run.Revision); Assert.Equal(resumed.Outcome, replay.Outcome);
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Request.TenantId, 200), a => a.Action == "operations.persona.apply");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Shared_executor_requires_current_generation_and_Runtime_suspension_for_authorization_requests(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var at = f.Clock.GetUtcNow();
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
            new AgentRunCommand.Claim(setup.Run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var gate = new TenantToolAuthorization((ITenantRunStore)f.Iam, new(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)), ApprovalService(f), f.RunConfiguration());
        var executor = new SessionToolExecutor(tenantAuthorization: gate);
        var call = new ModelToolCall("request", ToolCatalog.RequestAuthorization, "{}");
        var frame = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: run.AgentInstanceId, AgentRunId: run.AgentRunId,
            OwnedSessionId: run.SessionId, TenantId: setup.Tenant, AgentRunGeneration: run.Claim!.Generation);
        Assert.Equal(ToolPolicyDecision.Allow, await gate.PolicyAsync(run.SessionId, call, frame, default));
        Assert.Equal(ToolPolicyDecision.Deny, await gate.PolicyAsync(run.SessionId, call, frame with { AgentRunGeneration = f.Ids.NewId() }, default));
        Assert.Equal(ToolPolicyDecision.Deny, await gate.PolicyAsync(run.SessionId, call, null, default));
        var direct = await executor.ExecuteAsync(run.Admission.Configuration!.Definition, run.SessionId, call, 4000, admission: frame);
        Assert.Contains("authorization_requires_runtime_suspension", direct.Text);
        Assert.Empty(await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(setup.Tenant, null, null, 100));
    }
}
