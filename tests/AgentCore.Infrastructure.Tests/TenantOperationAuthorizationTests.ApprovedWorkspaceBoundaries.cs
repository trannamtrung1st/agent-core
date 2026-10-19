using System.Text;
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
    private static async Task<(Environment Setup, TenantRuntimeFrame Frame, TenantApprovalRequest Request)> ApprovedWorkspaceSuccessor(Fixture f, string tool, object arguments, bool exceptional = false, Func<Environment, Task>? seed = null)
    {
        var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s); await SeedRuntimeWorkspace(f, s, true);
        if (seed is not null) await seed(s);
        await GrantWorkspaceRuntimeReads(f, s, exceptional ? ["agents.read", "sessions.read", "workspaces.read", "workspaces.write"]
            : ["agents.read", "sessions.read", "workspaces.read", "workspaces.write", "workspaces.delete"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.write", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var call = new ModelToolCall("exact-mutation-authorization", ToolCatalog.RequestAuthorization,
            JsonSerializer.Serialize(new { operation = "mutate_workspace", mutation = new { tool, arguments } }));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 100000, 100), null), f.Ids.NewId());
        var intent = TenantApprovalToolContract.Parse(call).WorkspaceMutation!;
        var service = ApprovalService(f, workspaceMutations: MutationTargets(f));
        var request = (await service.PrepareWorkspaceMutationAsync(new(s.Tenant, frame.RunId), new(call.Id, intent.Tool, intent.ArgumentsJson), run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); Assert.True(TenantApprovalToolContract.Parse(call).Matches(request.Operation));
        request = await service.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, request.Revision);
        Assert.Equal(1, await service.DispatchPendingAsync());
        var source = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var successor = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        successor = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(successor.Owner, successor.AgentRunId,
            new AgentRunCommand.Claim(successor.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        return (s, new(successor.Owner, successor.SessionId, successor.AgentRunId, successor.Claim!.Generation), request);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_successor_seals_body_free_outcome_and_existing_continuation_never_replays(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var v = await ApprovedWorkspaceSuccessor(f, ToolCatalog.WorkspaceWrite, new { path = "/working/private.txt", content = "Saved exact successor bytes" });
        var service = WorkspaceEffects(f); var runtime = (ITenantRuntimeStore)f.Iam; var store = (ITenantRunStore)f.Iam;
        var run = (await store.FindTenantRunAsync(v.Setup.Tenant, v.Frame.RunId))!;
        var continuation = new TenantRunContinuationService(store, (ITenantApprovalStore)f.Iam, Effects(f), f.Clock, runtime, f.Ids, workspaceMutations: service);
        await continuation.PrepareExternalEffectAsync(run);
        run = (await store.FindTenantRunAsync(v.Setup.Tenant, v.Frame.RunId))!; Assert.True(run.Checkpoint!.AwaitingRuntimeAdmission);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages));
        var result = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        Assert.Contains("\"applied\":true", result); Assert.Contains("\"replayAllowed\":false", result); Assert.DoesNotContain("Saved exact successor bytes", result);
        Assert.DoesNotContain("Private model bytes", result);
        var resumed = await continuation.ResumeAsync(run); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome); Assert.False(resumed.Run.Checkpoint!.AwaitingRuntimeAdmission);
        await continuation.PrepareExternalEffectAsync(resumed.Run);
        Assert.Equal(1, (await f.Iam.ReadAuditAsync(v.Setup.Tenant, 200)).Count(a => a.Action == TenantWorkspaceEffectRules.Dispatch));
        var content = await WorkspaceReader(f).InspectTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working/private.txt", 1024);
        Assert.Equal("Saved exact successor bytes", Encoding.UTF8.GetString(content.Bytes));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_changed_source_denies_before_dispatch_and_revokes_exact_grant(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, true, true);
        await SeedRuntimeWorkspace(f, v.Setup, true, bytes: "Changed private source"u8.ToArray());
        var calls = 0; var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; }));
        var result = await ExecuteApprovedWorkspace(service, v.Frame, v.Request);
        Assert.Equal(TenantOperationOutcome.Denied, result.Outcome); Assert.Equal("workspace_source_changed", result.ReasonCode); Assert.Equal(0, calls);
        Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(v.Setup.Tenant, v.Request.RequestId))!.Status);
        Assert.Equal(result, await ExecuteApprovedWorkspace(service, v.Frame, v.Request));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(v.Setup.Tenant, 200), a => a.Action == TenantWorkspaceEffectRules.Dispatch);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_unknown_acknowledgement_is_terminal_and_does_not_replay(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, true, true); var calls = 0;
        var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; }, afterEffect: () => throw new IOException("Private native acknowledgement lost")));
        var result = await ExecuteApprovedWorkspace(service, v.Frame, v.Request);
        Assert.Equal(TenantOperationOutcome.Indeterminate, result.Outcome); Assert.Equal(TenantWorkspaceApprovedEffectRules.Uncertain, result.ReasonCode);
        Assert.Equal(OperationGrantStatus.Indeterminate, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(v.Setup.Tenant, v.Request.RequestId))!.Status);
        Assert.Equal(result, await ExecuteApprovedWorkspace(service, v.Frame, v.Request)); Assert.Equal(1, calls); Assert.Equal(0, await service.RecoverAsync());
        Assert.Equal("Exact reviewed literal replacement", Encoding.UTF8.GetString((await WorkspaceReader(f).InspectTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working/private.txt", 1024)).Bytes));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_unsettled_result_recovers_uncertain_without_native_replay(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T03:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, true, true); var calls = 0;
        var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; }), new UnavailableWorkspaceSettlement((ITenantWorkspaceOperationStore)f.Iam));
        Assert.Equal("workspace_effect_unsettled", (await Assert.ThrowsAsync<SecurityConflictException>(() => ExecuteApprovedWorkspace(service, v.Frame, v.Request).AsTask())).ReasonCode);
        Assert.Equal("workspace_effect_in_progress", (await Assert.ThrowsAsync<SecurityConflictException>(() => ExecuteApprovedWorkspace(service, v.Frame, v.Request).AsTask())).ReasonCode);
        Assert.Equal(1, calls); clock.Advance(TimeSpan.FromMinutes(6)); Assert.Equal(1, await WorkspaceEffects(f).RecoverAsync()); Assert.Equal(0, await WorkspaceEffects(f).RecoverAsync());
        var result = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(v.Setup.Tenant, v.Request.Operation.OperationId);
        Assert.Equal(TenantOperationOutcome.Indeterminate, result!.Outcome);
        Assert.Equal(OperationGrantStatus.Indeterminate, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(v.Setup.Tenant, v.Request.RequestId))!.Status);
    }
}
