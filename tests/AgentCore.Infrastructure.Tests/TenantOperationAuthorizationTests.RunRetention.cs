using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Session_deletion_minimizes_settled_Run_content_without_rewriting_approved_action_or_receipt(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await EffectSetup(f);
        var request = setup.Request; const string secret = "PRIVATE-SETTLED-APPROVAL-CHECKPOINT";
        var result = await Effects(f).ExecuteAsync(new(request.TenantId, setup.Run.AgentRunId), request.RequestId,
            request.Operation.OperationId, setup.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, result.Outcome);
        var approvals = (ITenantApprovalStore)f.Iam;
        var approved = JsonSerializer.Serialize(await approvals.FindApprovalAsync(request.TenantId, request.RequestId));
        var receipt = JsonSerializer.Serialize(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(request.TenantId, setup.Run.AgentRunId))!;
        var previous = run.Checkpoint ?? new AgentRunCheckpoint("{}", 0, 0, 10000);
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.User, secret)]),
            previous.StepCount, previous.OutputBytes, previous.RemainingOverallBudgetMs, previous.ActiveExecutionMs, previous.BudgetRecordedAtUtc);
        run = run.SaveCheckpoint(run.Revision, setup.Generation, checkpoint, secret, f.Clock.GetUtcNow());
        run = run.Complete(run.Revision, setup.Generation, secret, f.Clock.GetUtcNow(), outcomeKind: AgentRunOutcomeKind.NoAction);
        await SaveEffectTestRun(f, run);
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var actor = setup.Environment.Owner with { AuthenticationSessionId = login.SessionId };
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(request.TenantId, run.SessionId))!;
        await f.Sessions.ChangeAsync(request.TenantId, actor, new(run.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete),
            (await f.Iam.ReadTenantAsync(request.TenantId))!.Tenant.Revision);
        AgentRun retained;
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            var row = await db.AgentRuns.SingleAsync(r => r.AgentRunId == run.AgentRunId.ToString("D"));
            Assert.DoesNotContain(secret, row.PayloadJson); retained = AgentRunStoreMapping.ToDomain(row);
        }
        else retained = ((InMemoryMemoryStore)f.Memory).ReadRetainedTenantOperationRun(request.TenantId, run.AgentRunId)!;
        Assert.DoesNotContain(secret, AgentRunStoreMapping.ToRecord(retained).PayloadJson);
        Assert.Equal(AgentRunStatus.Completed, retained.Status); Assert.Equal(run.Result!.OutcomeKind, retained.Result!.OutcomeKind);
        Assert.Equal(run.Result.CompletedAtUtc, retained.Result.CompletedAtUtc);
        Assert.Equal(JsonSerializer.Serialize(run.Admission), JsonSerializer.Serialize(retained.Admission));
        Assert.Equal(run.ApprovalRequestId, retained.ApprovalRequestId);
        Assert.Equal(request.Operation.ActionHash, retained.SideEffect.ActionHash);
        Assert.Equal(AgentRunSideEffectDisposition.Succeeded, retained.SideEffect.Disposition);
        Assert.Equal(previous.StepCount, retained.Checkpoint!.StepCount);
        Assert.Equal(previous.RemainingOverallBudgetMs, retained.Checkpoint.RemainingOverallBudgetMs);
        Assert.Equal(approved, JsonSerializer.Serialize(await approvals.FindApprovalAsync(request.TenantId, request.RequestId)));
        Assert.Equal(receipt, JsonSerializer.Serialize(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId)));
    }
}
