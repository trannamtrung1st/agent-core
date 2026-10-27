using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static async Task<Guid> WaitReadAuthority(Fixture f, BackgroundSetup parent)
    {
        var tenant = parent.Setup.Tenant;
        var role = await f.Access.SaveRoleAsync(parent.Setup.Owner.PrincipalId, tenant, null, "Independent result reader", ["sessions.read", "agent-runs.read"], (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        return (await f.Access.AssignRoleAsync(parent.Setup.Owner.PrincipalId, tenant, parent.Setup.Agent.PrincipalId, role.RoleId,
            AccessScope.ForTenant(tenant), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision)).AssignmentId;
    }
    private static async Task<(AgentRun Run, AgentRunCommand.SuspendWait Command)> WaitCheckpoint(Fixture f, BackgroundSetup parent, Guid? task, double seconds = 10)
    {
        var args = task is { } id ? JsonSerializer.Serialize(new { mode = "tasks", taskIds = new[] { id }, until = "all", timeoutSeconds = seconds })
            : JsonSerializer.Serialize(new { mode = "duration", seconds });
        var call = new ModelToolCall("exact-wait", ToolCatalog.ExecutionWait, args);
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 100000, 100, f.Clock.GetUtcNow());
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId,
            new AgentRunCommand.Checkpoint(parent.Parent.Revision, f.Clock.GetUtcNow(), parent.Frame.Generation, checkpoint, null), f.Ids.NewId());
        using var json = JsonDocument.Parse(args); var wait = ExecutionWaitContract.Parse(call.Id, json.RootElement, f.Clock.GetUtcNow());
        return (run, new(run.Revision, f.Clock.GetUtcNow(), parent.Frame.Generation, checkpoint, wait));
    }

    [Theory] [InlineData(false, "duration")] [InlineData(true, "duration")]
    [InlineData(false, "terminal")] [InlineData(true, "terminal")]
    [InlineData(false, "timeout")] [InlineData(true, "timeout")]
    [InlineData(false, "message")] [InlineData(true, "message")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    public async Task Tenant_Task_wait_reopens_fences_old_generation_and_resumes_same_run_once(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-28T01:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f);
        var runtime = (ITenantRuntimeStore)f.Iam; var tasks = (ITenantTaskStore)f.Iam;
        var child = await BackgroundChild(f, parent); await AdmitTaskFixture(f, parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var pending = await WaitCheckpoint(f, parent, mode == "duration" ? null : child.Admission.Task.TaskId);
        var waiting = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId, pending.Command, f.Ids.NewId(), runtimeGeneration: parent.Frame.Generation);
        Assert.Equal(AgentRunStatus.WaitingForSignal, waiting.Status); Assert.Null(waiting.Claim);
        Assert.Equal(parent.Frame.Generation, waiting.Wait!.SuspendedGeneration);
        Assert.DoesNotContain(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 100), r => r.AgentRunId == waiting.AgentRunId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.LoadTenantRuntimeAsync(parent.Frame).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId,
            new AgentRunCommand.Checkpoint(waiting.Revision, clock.GetUtcNow(), parent.Frame.Generation, pending.Command.Value, null), f.Ids.NewId(), runtimeGeneration: parent.Frame.Generation).AsTask());
        if (mode == "terminal")
        {
            var active = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, active.Run, active.Snapshot);
            await runtime.CommitTenantOutcomeAsync(active.Frame, outcome.Snapshot, active.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
            Assert.True(await tasks.ReconcileTaskAsync(parent.Setup.Tenant, child.Admission.Task.TaskId, 1, f.Ids.NewId()));
        }
        if (mode == "message")
        {
            var worker = await ClaimBackground(f, child.Run);
            await ((ITenantAgentMessageStore)f.Iam).AppendAgentMessageAsync(new(parent.Setup.Tenant,
                new(Agent: worker.Frame, Tool: "tasks.messages.send"), child.Admission.Task.TaskId, null, null, AgentMessageKind.Question,
                "Which reviewed source should I use?", "worker-question", (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision,
                f.Ids.NewId(), f.Ids.NewId(), f.Ids.NewId()));
        }
        if (mode == "revoked") await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant,
            parent.Assignment!.Value, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        clock.Advance(TimeSpan.FromSeconds(mode is "duration" or "timeout" ? 10 : 1));
        if (sqlite) runtime = new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: clock);
        Assert.Contains(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 100), r => r.AgentRunId == waiting.AgentRunId);
        var generation = f.Ids.NewId(); var wake = new AgentRunCommand.ResumeWait(waiting.Revision, clock.GetUtcNow(), generation, clock.GetUtcNow().AddMinutes(5));
        var resumed = await runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId, wake, f.Ids.NewId());
        Assert.Equal(waiting.AgentRunId, resumed.AgentRunId); Assert.Equal(waiting.ActivationId, resumed.ActivationId); Assert.Equal(waiting.ResponseId, resumed.ResponseId);
        Assert.Equal(waiting.AttemptCount, resumed.AttemptCount); Assert.Equal(1, resumed.WaitCount); Assert.Equal(generation, resumed.Claim!.Generation);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Checkpoint, out var messages));
        var result = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.ToolCallId == "exact-wait");
        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal(mode == "duration" ? "elapsed" : mode == "timeout" ? "timeout" : mode == "revoked" ? "unavailable" : "condition_met", json.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("sourceAgentRunId", result.Text); Assert.DoesNotContain("backgroundSessionId", result.Text);
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId, wake, f.Ids.NewId()).AsTask());
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantCompletionAsync(child.Run.Owner, child.Run.AgentRunId));
    }
}
