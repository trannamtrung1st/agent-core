using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static async Task<Guid> WaitReadAuthority(Fixture f, BackgroundSetup parent)
    {
        var tenant = parent.Setup.Tenant; async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(parent.Setup.Owner.PrincipalId, tenant, null, "Exact child wait reader", ["sessions.read", "agent-runs.read"], await Revision());
        return (await f.Access.AssignRoleAsync(parent.Setup.Owner.PrincipalId, tenant, parent.Setup.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision())).AssignmentId;
    }

    private static async Task<(AgentRun Run, AgentRunCommand.SuspendWait Command)> WaitCheckpoint(Fixture f, BackgroundSetup parent, Guid? child, double seconds = 10)
    {
        var args = child is { } id ? JsonSerializer.Serialize(new { mode = "background", backgroundSessionIds = new[] { id }, until = "all", timeoutSeconds = seconds })
            : JsonSerializer.Serialize(new { mode = "duration", seconds });
        var call = new ModelToolCall("exact-wait", ToolCatalog.ExecutionWait, args);
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 100000, 100, f.Clock.GetUtcNow());
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId,
            new AgentRunCommand.Checkpoint(parent.Parent.Revision, f.Clock.GetUtcNow(), parent.Frame.Generation, checkpoint, null), f.Ids.NewId());
        using var json = JsonDocument.Parse(args); var wait = ExecutionWaitContract.Parse(call.Id, json.RootElement, f.Clock.GetUtcNow());
        return (run, new(run.Revision, f.Clock.GetUtcNow(), parent.Frame.Generation, checkpoint, wait));
    }

    [Theory] [InlineData(false, "duration")] [InlineData(true, "duration")]
    [InlineData(false, "background")] [InlineData(true, "background")]
    [InlineData(false, "timeout")] [InlineData(true, "timeout")]
    public async Task Tenant_typed_wait_reopens_with_same_identity_one_result_fresh_generation_and_no_completion_consumption(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T03:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); await WaitReadAuthority(f, parent);
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var child = await BackgroundChild(f, parent);
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var pending = await WaitCheckpoint(f, parent, mode == "duration" ? null : child.Session.SessionId);
        var waiting = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId, pending.Command, f.Ids.NewId(), runtimeGeneration: parent.Frame.Generation);
        Assert.Equal(AgentRunStatus.WaitingForSignal, waiting.Status); Assert.Null(waiting.Claim); Assert.Equal(parent.Frame.Generation, waiting.Wait!.SuspendedGeneration);
        Assert.DoesNotContain(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 100), r => r.AgentRunId == waiting.AgentRunId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.LoadTenantRuntimeAsync(parent.Frame).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId,
            new AgentRunCommand.Checkpoint(waiting.Revision, clock.GetUtcNow(), parent.Frame.Generation, pending.Command.Value, null), f.Ids.NewId(), runtimeGeneration: parent.Frame.Generation).AsTask());
        if (sqlite) runtime = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: clock);
        if (mode == "background")
        {
            var active = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, active.Run, active.Snapshot);
            await runtime.CommitTenantOutcomeAsync(active.Frame, outcome.Snapshot, active.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        else clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Contains(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 100), r => r.AgentRunId == waiting.AgentRunId);
        var generation = f.Ids.NewId(); var wake = new AgentRunCommand.ResumeWait(waiting.Revision, clock.GetUtcNow(), generation, clock.GetUtcNow().AddMinutes(5));
        var resumed = await runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId, wake, f.Ids.NewId());
        Assert.Equal(waiting.AgentRunId, resumed.AgentRunId); Assert.Equal(waiting.ActivationId, resumed.ActivationId); Assert.Equal(waiting.ResponseId, resumed.ResponseId);
        Assert.Equal(1, resumed.AttemptCount); Assert.Equal(1, resumed.WaitCount); Assert.Equal(generation, resumed.Claim!.Generation); Assert.True(resumed.Checkpoint!.AwaitingSignalAdmission);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Checkpoint, out var messages));
        var result = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.ToolCallId == "exact-wait");
        using var json = JsonDocument.Parse(result.Text); Assert.Equal(mode == "background" ? "condition_met" : mode == "duration" ? "elapsed" : "timeout", json.RootElement.GetProperty("reason").GetString());
        await Assert.ThrowsAsync<AgentCoreException>(() => runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId, wake, f.Ids.NewId()).AsTask());
        if (mode == "background") Assert.Equal(CompletionInboxStatus.Pending, (await runs.FindTenantCompletionAsync(child.Run.Owner, child.Run.AgentRunId))!.Status);
        else Assert.Null(await runs.FindTenantCompletionAsync(child.Run.Owner, child.Run.AgentRunId));
    }

    [Theory] [InlineData(false, "missing-read")] [InlineData(true, "missing-read")]
    [InlineData(false, "foreign")] [InlineData(true, "foreign")]
    [InlineData(false, "forged")] [InlineData(true, "forged")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "policy")] [InlineData(true, "policy")]
    [InlineData(false, "policy-revoked")] [InlineData(true, "policy-revoked")]
    public async Task Tenant_wait_source_and_saved_action_fences_refuse_or_wake_without_disclosing_current_status(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T03:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f);
        Guid? read = boundary == "missing-read" ? null : await WaitReadAuthority(f, parent);
        var runtime = (ITenantRuntimeStore)f.Iam; var child = await BackgroundChild(f, parent);
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        async Task RequireReview() => await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, clock)
            .SaveAsync(parent.Setup.Tenant, parent.Setup.Owner, "agent-runs.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, 0);
        if (boundary == "policy") await RequireReview();
        var pending = await WaitCheckpoint(f, parent, boundary == "foreign" ? f.Ids.NewId() : child.Session.SessionId);
        var command = boundary == "forged" ? pending.Command with { Wait = pending.Command.Wait with { DeadlineUtc = pending.Command.Wait.DeadlineUtc.AddSeconds(1) } } : pending.Command;
        if (boundary is not ("revoked" or "policy-revoked"))
        {
            var error = await Assert.ThrowsAsync<AgentCoreException>(() => runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId, command, f.Ids.NewId()).AsTask());
            Assert.Equal(boundary == "forged" ? "Conflict" : "NotFound", error.Code);
            Assert.Equal(pending.Run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant, parent.Parent.AgentRunId))!.Revision); return;
        }
        var waiting = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId, command, f.Ids.NewId());
        if (boundary == "policy-revoked") await RequireReview();
        else await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, read!.Value, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        Assert.Contains(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 100), r => r.AgentRunId == waiting.AgentRunId);
        var resumed = await runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId, new AgentRunCommand.ResumeWait(waiting.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Checkpoint, out var messages));
        using var result = JsonDocument.Parse(Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text);
        Assert.Equal("unavailable", result.RootElement.GetProperty("reason").GetString());
        var status = Assert.Single(result.RootElement.GetProperty("statuses").EnumerateArray());
        Assert.Equal("Unavailable", status.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, status.GetProperty("sourceAgentRunId").ValueKind);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_signal_dispatch_deferral_and_pre_admission_recovery_preserve_result_and_attempt(bool sqlite, bool recover)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T03:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); var runtime = (ITenantRuntimeStore)f.Iam;
        var pending = await WaitCheckpoint(f, parent, null, 1);
        var waiting = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId, pending.Command, f.Ids.NewId()); clock.Advance(TimeSpan.FromSeconds(1));
        var active = await runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId, new AgentRunCommand.ResumeWait(waiting.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        if (recover) clock.Advance(TimeSpan.FromMinutes(6));
        AgentRunCommand defer = recover ? new AgentRunCommand.Recover(active.Revision, clock.GetUtcNow())
            : new AgentRunCommand.DeferSignalDispatch(active.Revision, clock.GetUtcNow(), active.Claim!.Generation, clock.GetUtcNow().AddSeconds(1));
        var deferred = await runtime.ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId, defer, f.Ids.NewId());
        Assert.Equal(AgentRunStatus.WaitingToRetry, deferred.Status); Assert.Equal(active.Checkpoint!.PayloadJson, deferred.Checkpoint!.PayloadJson); Assert.Equal(1, deferred.AttemptCount);
        Assert.Equal(active.Checkpoint.RemainingOverallBudgetMs, deferred.Checkpoint.RemainingOverallBudgetMs);
        clock.Advance(TimeSpan.FromSeconds(1)); var resumed = await ClaimBackground(f, deferred);
        Assert.Equal(1, resumed.Run.AttemptCount); Assert.Equal(1, resumed.Run.WaitCount); Assert.NotEqual(active.Claim!.Generation, resumed.Frame.Generation);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages)); Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ExecutionWait);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_wait_observation_is_rechecked_before_provider_context_after_source_read_revocation(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T03:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); var assignment = await WaitReadAuthority(f, parent);
        var runtime = (ITenantRuntimeStore)f.Iam; var child = await BackgroundChild(f, parent); await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var active = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, active.Run, active.Snapshot); await runtime.CommitTenantOutcomeAsync(active.Frame, outcome.Snapshot, active.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        var pending = await WaitCheckpoint(f, parent, child.Run.SessionId);
        var waiting = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId, pending.Command, f.Ids.NewId());
        var resumed = await runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId, new AgentRunCommand.ResumeWait(waiting.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var guard = new TenantWaitSourceAuthority(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.Authorizer);
        await guard.RequireStoredAsync(resumed, default);
        await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, assignment, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        Assert.Equal("wait_source_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => guard.RequireStoredAsync(resumed, default).AsTask())).ReasonCode);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_wait_follows_only_exact_child_approval_handoffs_and_does_not_treat_Continued_as_finished(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T03:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); var child = await BackgroundChild(f, parent);
        await ((ITenantRuntimeStore)f.Iam).AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var now = f.Clock.GetUtcNow(); var claimed = child.Run.TakeClaim(f.Ids.NewId(), now, now.AddMinutes(5));
        var call = new ModelToolCall("consent", ToolCatalog.RequestAuthorization, "{}");
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 100000);
        claimed = claimed.SaveCheckpoint(claimed.Revision, claimed.Claim!.Generation, checkpoint, null, now);
        var request = f.Ids.NewId(); var waiting = claimed.WaitForDurableApproval(claimed.Revision, request, claimed.Claim!.Generation, now);
        var nextId = f.Ids.NewId(); var activationId = f.Ids.NewId(); var previous = waiting.Admission.ExecutionContext!;
        var activation = new Activation(activationId, waiting.SessionId, ActivationKind.ApprovalContinuation, [], f.Ids.NewId(), null, waiting.SessionId,
            waiting.AgentRunId, "approval-continuation:" + request.ToString("D"), now, previous.RootActivationId, waiting.ActivationId,
            JsonSerializer.Serialize(new AgentRunAdmissionFactory.SignalInput(TriggerKind.ManualInvocation, "Continue the exact task", "approval")));
        var context = new TrustedExecutionContext(previous.TenantId, previous.AgentPrincipalId, previous.OriginalInitiatorPrincipalId, waiting.SessionId, nextId,
            activationId, previous.RootActivationId, waiting.AgentRunId, waiting.ActivationId, ExecutionOrigin.ApprovalContinuation);
        var old = waiting.Admission;
        var admission = new AgentRunAdmission(activation, old.DefinitionId, old.DefinitionVersion, old.PinnedPersona, old.ResponseId, old.OutputContract, old.ExecutionBudget,
            old.Configuration, old.ComposerInput, old.TaskIntent, context, new(waiting.AgentRunId, request, f.Ids.NewId(), new string('a', 64), TenantApprovalContinuationRules.Inputs(waiting)));
        var next = waiting.ForkApprovalContinuation(nextId, admission, request, now);
        var handed = waiting.HandoffApprovalContinuation(waiting.Revision, request, nextId, now);
        var rows = new Dictionary<Guid, AgentRun> { [handed.AgentRunId] = handed, [next.AgentRunId] = next };
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, child.Run.SessionId))!;
        var candidate = (await TenantWaitRules.ChildAsync(parent.Parent, session, (id, _) => ValueTask.FromResult(rows.GetValueOrDefault(id)), default))!;
        var pendingParent = await WaitCheckpoint(f, parent, child.Run.SessionId, 20);
        var suspendedParent = pendingParent.Command.Apply(pendingParent.Run, f.Ids.NewId);
        Assert.Equal(handed.AgentRunId, candidate.Run.AgentRunId); Assert.Equal(next.AgentRunId, candidate.CurrentTaskRun!.AgentRunId);
        Assert.False(AgentRunWaitExecution.IsReady(suspendedParent, now, [candidate]));
        var running = next.TakeClaim(f.Ids.NewId(), now, now.AddMinutes(5));
        var finished = new AgentRunCommand.Complete(running.Revision, now, running.Claim!.Generation, "Task finished", AgentRunOutcomeKind.NoAction, null).Apply(running, f.Ids.NewId);
        Assert.True(AgentRunWaitExecution.IsReady(suspendedParent, now, [candidate with { CurrentTaskRun = finished }]));
        rows.Remove(next.AgentRunId);
        Assert.Null(await TenantWaitRules.ChildAsync(parent.Parent, session, (id, _) => ValueTask.FromResult(rows.GetValueOrDefault(id)), default));
    }
}
