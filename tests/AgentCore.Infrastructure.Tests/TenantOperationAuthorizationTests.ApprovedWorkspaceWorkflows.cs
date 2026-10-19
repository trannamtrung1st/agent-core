using System.Text;
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
    [Theory] [InlineData(false, "workspace.patch")] [InlineData(true, "workspace.patch")]
    [InlineData(false, "workspace.mkdir")] [InlineData(true, "workspace.mkdir")]
    [InlineData(false, "workspace.copy")] [InlineData(true, "workspace.copy")]
    [InlineData(false, "workspace.move")] [InlineData(true, "workspace.move")]
    [InlineData(false, "workspace.delete")] [InlineData(true, "workspace.delete")]
    [InlineData(false, "copy-tree")] [InlineData(true, "copy-tree")]
    public async Task Tenant_approved_workspace_effect_supported_native_operations_use_only_saved_parameters(bool sqlite, string tool)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        object args = tool switch {
            ToolCatalog.WorkspacePatch => new { path = "/working/private.txt", edits = new[] { new { oldText = "Private model bytes", newText = "Exact patched bytes" } } },
            ToolCatalog.WorkspaceMkdir => new { path = "/working/new" },
            ToolCatalog.WorkspaceCopy => new { source = "/working/private.txt", destination = "/home/copy.txt" },
            ToolCatalog.WorkspaceMove => new { source = "/working/private.txt", destination = "/working/moved.txt" },
            "copy-tree" => new { source = "/working/tree", destination = "/home/copied-tree" },
            _ => new { path = "/working/private.txt" } };
        async Task Seed(Environment s)
        {
            await SeedRuntimeWorkspace(f, s, true, "tree/a.txt");
            await WorkspaceReader(f).StructureTenantAsync(new(s.Tenant, s.Agent.Instance.InstanceId, s.Run.SessionId), [new("mkdir", Path: "/working/tree/empty")], _ => ValueTask.CompletedTask);
        }
        var v = await ApprovedWorkspaceSuccessor(f, tool == "copy-tree" ? ToolCatalog.WorkspaceCopy : tool, args, seed: tool == "copy-tree" ? Seed : null); var service = WorkspaceEffects(f);
        var result = await ExecuteApprovedWorkspace(service, v.Frame, v.Request); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome);
        Assert.Equal(result, await ExecuteApprovedWorkspace(service, v.Frame, v.Request));
        var owner = new TenantWorkspaceReference(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId); var files = WorkspaceReader(f);
        if (tool == ToolCatalog.WorkspaceMkdir) Assert.Contains((await files.ListTenantAsync(owner, "/working", null, 100)).Items, i => i.LogicalPath == "/working/new" && i.Directory);
        else if (tool == ToolCatalog.WorkspaceDelete) Assert.DoesNotContain((await files.ListTenantAsync(owner, "/working", null, 100)).Items, i => i.LogicalPath == "/working/private.txt");
        else if (tool == "copy-tree")
        {
            var home = owner with { SessionId = null };
            Assert.Contains((await files.ListTenantAsync(home, "/home/copied-tree", null, 100)).Items, i => i.LogicalPath == "/home/copied-tree/empty" && i.Directory);
            Assert.Equal("Private model bytes: thử nghiệm", Encoding.UTF8.GetString((await files.InspectTenantAsync(home, "/home/copied-tree/a.txt", 1024)).Bytes));
        }
        else
        {
            var path = tool == ToolCatalog.WorkspaceCopy ? "/home/copy.txt" : tool == ToolCatalog.WorkspaceMove ? "/working/moved.txt" : "/working/private.txt";
            var bytes = await files.InspectTenantAsync(tool == ToolCatalog.WorkspaceCopy ? owner with { SessionId = null } : owner, path, 1024);
            Assert.Equal(tool == ToolCatalog.WorkspacePatch ? "Exact patched bytes: thử nghiệm" : "Private model bytes: thử nghiệm", Encoding.UTF8.GetString(bytes.Bytes));
            if (tool == ToolCatalog.WorkspaceMove) Assert.DoesNotContain((await files.ListTenantAsync(owner, "/working", null, 100)).Items, i => i.LogicalPath == "/working/private.txt");
        }
        Assert.Equal(1, (await f.Iam.ReadAuditAsync(v.Setup.Tenant, 200)).Count(a => a.Action == TenantWorkspaceEffectRules.Dispatch));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_partial_batch_retains_prefix_and_seals_truthful_unknown_without_retry(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var v = await ApprovedWorkspaceSuccessor(f, ToolCatalog.WorkspaceBatch, new { operations = new object[] {
            new { op = "mkdir", path = "/working/prefix" }, new { op = "move", source = "/working/missing", destination = "/working/moved" }, new { op = "mkdir", path = "/working/later" } } });
        var service = WorkspaceEffects(f); var result = await ExecuteApprovedWorkspace(service, v.Frame, v.Request); Assert.Equal(TenantOperationOutcome.Indeterminate, result.Outcome);
        var owner = new TenantWorkspaceReference(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId);
        var listing = await WorkspaceReader(f).ListTenantAsync(owner, "/working", null, 100);
        Assert.Contains(listing.Items, i => i.LogicalPath == "/working/prefix"); Assert.DoesNotContain(listing.Items, i => i.LogicalPath is "/working/later" or "/working/moved");
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(v.Setup.Tenant, v.Frame.RunId))!;
        Assert.True(run.Checkpoint!.AwaitingRuntimeAdmission); Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages));
        var text = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        Assert.Contains("\"applied\":null", text); Assert.Contains("\"partialEffectsPossible\":true", text); Assert.Contains("\"replayAllowed\":false", text);
        Assert.Equal(result, await ExecuteApprovedWorkspace(service, v.Frame, v.Request)); Assert.Equal(0, await service.RecoverAsync());
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_approved_workspace_effect_current_independent_policy_requalifies_original_reviewer_and_exact_grantor(bool sqlite, bool exceptional)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, true, exceptional);
        await Policies(f).SaveAsync(v.Setup.Tenant, v.Setup.Owner, "workspaces.write", new(OperationApprovalMode.Always, true), (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision, exceptional ? 0 : 1);
        var calls = 0; var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; }));
        var result = await ExecuteApprovedWorkspace(service, v.Frame, v.Request);
        Assert.Equal(TenantOperationOutcome.Denied, result.Outcome); Assert.Equal(exceptional ? "grantor_authority_lost" : "independent_reviewer_required", result.ReasonCode); Assert.Equal(0, calls);
        if (exceptional) Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(v.Setup.Tenant, v.Request.RequestId))!.Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_expired_consent_with_live_claim_is_denied_without_native_dispatch(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T03:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var v = await MutationReviewFixture(f, true, true); clock.Advance(TimeSpan.FromMinutes(59));
        var request = await v.Approvals.DecideAsync(v.Setup.Tenant, v.Request.RequestId, v.Setup.Owner, true, v.Request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision, v.Request.Revision);
        await v.Approvals.DispatchPendingAsync(); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(v.Setup.Tenant, v.Frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        clock.Advance(TimeSpan.FromMinutes(2)); var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        var calls = 0; var result = await ExecuteApprovedWorkspace(WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; })), frame, request);
        Assert.Equal(TenantOperationOutcome.Denied, result.Outcome); Assert.Equal("approval_expired", result.ReasonCode); Assert.Equal(0, calls);
        Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(v.Setup.Tenant, request.RequestId))!.Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_cancellation_seals_unknown_before_noncooperative_native_task_returns(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, true, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource(); var calls = 0;
        var files = new DelayedApprovedWorkspaceWrite(WorkspaceReader(f), async () => { calls++; entered.TrySetResult(); await proceed.Task; });
        var service = WorkspaceEffects(f, files);
        var pending = ExecuteApprovedWorkspace(service, v.Frame, v.Request, cancel.Token).AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(v.Setup.Tenant, v.Request.Operation.OperationId))!;
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome); Assert.Equal(OperationGrantStatus.Indeterminate, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(v.Setup.Tenant, v.Request.RequestId))!.Status);
        proceed.TrySetResult(); await files.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(receipt, await ExecuteApprovedWorkspace(service, v.Frame, v.Request)); Assert.Equal(1, calls);
        Assert.Equal("Private model bytes: thử nghiệm", Encoding.UTF8.GetString((await WorkspaceReader(f).InspectTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working/private.txt", 1024)).Bytes));
    }

    private sealed class DelayedApprovedWorkspaceWrite(ITenantWorkspaceMutationStore inner, Func<Task> wait) : ITenantWorkspaceMutationStore
    {
        public ValueTask TransformOfficeTenantAsync(TenantWorkspaceReference owner, string path, bool create, Func<ReadOnlyMemory<byte>, CancellationToken, byte[]> process, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.TransformOfficeTenantAsync(owner, path, create, process, authorizeCommit, ct);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WriteTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> check, CancellationToken ct = default)
        { try { await wait(); await inner.WriteTenantAsync(owner, path, bytes, check, ct); } finally { Finished.TrySetResult(); } }
        public ValueTask DeleteTenantAsync(TenantWorkspaceReference owner, string path, Func<CancellationToken, ValueTask> check, CancellationToken ct = default) => inner.DeleteTenantAsync(owner, path, check, ct);
        public ValueTask<WorkspacePatchResult> PatchTenantAsync(TenantWorkspaceReference owner, string path, IReadOnlyList<WorkspaceTextEdit> edits, Func<CancellationToken, ValueTask> check, CancellationToken ct = default) => inner.PatchTenantAsync(owner, path, edits, check, ct);
        public ValueTask<WorkspaceStructureResult> StructureTenantAsync(TenantWorkspaceReference owner, IReadOnlyList<WorkspaceStructuralOperation> operations, Func<CancellationToken, ValueTask> check, CancellationToken ct = default) => inner.StructureTenantAsync(owner, operations, check, ct);
        public ValueTask CopyTenantAsync(TenantWorkspaceReference source, string sourcePath, TenantWorkspaceReference destination, string destinationPath, Func<CancellationToken, ValueTask> check, CancellationToken ct = default) => inner.CopyTenantAsync(source, sourcePath, destination, destinationPath, check, ct);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_unusable_delivery_checkpoint_cannot_erase_completed_facts_or_replay_files(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var v = await ApprovedWorkspaceSuccessor(f, ToolCatalog.WorkspaceWrite, new { path = "/working/private.txt", content = "Committed before checkpoint changed" });
        var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), afterEffect: async () => {
            var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(v.Setup.Tenant, v.Frame.RunId))!;
            var budget = run.Checkpoint!.PauseBudgetClock(f.Clock.GetUtcNow());
            var call = new ModelToolCall("different", ToolCatalog.RequestAuthorization, """{"operation":"mutate_workspace","mutation":{"tool":"workspace.mkdir","arguments":{"path":"/working/different"}}}""");
            await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), v.Frame.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), budget.StepCount, budget.OutputBytes, budget.RemainingOverallBudgetMs, budget.ActiveExecutionMs), null), f.Ids.NewId());
        }));
        var receipt = await ExecuteApprovedWorkspace(service, v.Frame, v.Request); Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
        Assert.Equal(receipt, await ExecuteApprovedWorkspace(service, v.Frame, v.Request)); Assert.Equal(0, await service.RecoverAsync());
        Assert.Equal("Committed before checkpoint changed", Encoding.UTF8.GetString((await WorkspaceReader(f).InspectTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working/private.txt", 1024)).Bytes));
    }
}
