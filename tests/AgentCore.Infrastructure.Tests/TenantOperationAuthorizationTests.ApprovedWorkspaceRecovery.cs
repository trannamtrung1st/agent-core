using System.Text;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static async Task<TenantApprovedWorkspaceDispatch> StageApprovedWorkspace(Fixture f, TenantRuntimeFrame frame, TenantApprovalRequest request)
    {
        var state = (await f.Iam.ReadTenantAsync(request.TenantId))!; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(request.TenantId, frame.RunId))!;
        var decision = (await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(request.TenantId, request.RequestId))!;
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(request.TenantId, request.RequestId);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, frame.Generation, run.Revision, request.Revision,
            decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, "operation_completed");
        return (await ((ITenantWorkspaceOperationStore)f.Iam).TryStartApprovedWorkspaceEffectAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } },
            state.Tenant.Revision, commit, new(f.Ids.NewId(), request.TenantId, request.ExecutorPrincipalId, TenantWorkspaceEffectRules.Dispatch,
                request.ResourceId, "allowed", "workspace_mutation_dispatched", f.Clock.GetUtcNow())))!.Fence!;
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_approved_workspace_effect_current_Review_permission_loss_cannot_become_an_exception_before_or_at_native_commit(bool sqlite, bool atGate)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, true, false); var s = v.Setup;
        async Task RevokeWrite()
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var assignment = Assert.Single(state.Assignments, a => a.PrincipalId == s.Agent.PrincipalId);
            await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
            await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read"]);
        }
        if (!atGate) await RevokeWrite();
        var calls = 0; var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; }, beforeCommit: atGate ? RevokeWrite : null));
        var result = await ExecuteApprovedWorkspace(service, v.Frame, v.Request);
        Assert.Equal(atGate ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Denied, result.Outcome);
        if (!atGate) Assert.Equal("operation_authority_changed", result.ReasonCode);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId));
        Assert.Equal("Private model bytes: thử nghiệm", Encoding.UTF8.GetString((await WorkspaceReader(f).InspectTenantAsync(WorkspaceOwner(s, true), "/working/private.txt", 1024)).Bytes));
        Assert.Equal(result, await ExecuteApprovedWorkspace(service, v.Frame, v.Request)); Assert.Equal(atGate ? 1 : 0, calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_private_source_change_at_native_commit_is_uncertain_and_never_overwrites_or_replays(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, true, true);
        var calls = 0; var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; },
            beforeCommit: () => SeedRuntimeWorkspace(f, v.Setup, true, bytes: "Concurrent private replacement"u8.ToArray())));
        var result = await ExecuteApprovedWorkspace(service, v.Frame, v.Request); Assert.Equal(TenantOperationOutcome.Indeterminate, result.Outcome);
        Assert.Equal("Concurrent private replacement", Encoding.UTF8.GetString((await WorkspaceReader(f).InspectTenantAsync(WorkspaceOwner(v.Setup, true), "/working/private.txt", 1024)).Bytes));
        Assert.Equal(result, await ExecuteApprovedWorkspace(service, v.Frame, v.Request)); Assert.Equal(1, calls);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_approved_workspace_effect_abandoned_fence_recovers_with_retained_owner_after_access_loss_or_Session_deletion(bool sqlite, bool deleted)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T03:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, true, true); var s = v.Setup;
        var fence = await StageApprovedWorkspace(f, v.Frame, v.Request);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var assignment = Assert.Single(state.Assignments, a => a.PrincipalId == s.Agent.PrincipalId);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
        if (deleted)
        {
            await CancelArtifactRun(f, s, v.Frame);
            var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, v.Frame.SessionId))!;
            await f.Sessions.ChangeAsync(s.Tenant, s.Owner, new(v.Frame.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, v.Frame.RunId));
        }
        else clock.Advance(TimeSpan.FromMinutes(6));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Equal(fence, Assert.Single(await ((ITenantWorkspaceOperationStore)reopened).RecoverableApprovedWorkspaceEffectsAsync(clock.GetUtcNow(), 100)));
        }
        var calls = 0; var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; }));
        Assert.Equal(1, await service.RecoverAsync()); Assert.Equal(0, await service.RecoverAsync()); Assert.Equal(0, calls);
        var result = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId);
        Assert.Equal(TenantOperationOutcome.Indeterminate, result!.Outcome); Assert.Equal(v.Frame.RunId, result.RunId); Assert.Equal(v.Request.RequestId, result.RequestId);
        Assert.Equal(OperationGrantStatus.Indeterminate, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId))!.Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_concurrent_callers_cannot_share_dispatch_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, true, true); var calls = 0;
        var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }));
        async Task Attempt() { try { await ExecuteApprovedWorkspace(service, v.Frame, v.Request); } catch (SecurityConflictException) { } }
        await Task.WhenAll(Attempt(), Attempt());
        Assert.Equal(TenantOperationOutcome.Completed, (await ExecuteApprovedWorkspace(service, v.Frame, v.Request)).Outcome); Assert.Equal(1, calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_approved_workspace_effect_mixed_batch_uses_primary_delete_grant_and_does_not_invalidate_its_own_ordered_prefix(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var v = await ApprovedWorkspaceSuccessor(f, ToolCatalog.WorkspaceBatch, new { operations = new object[] {
            new { op = "delete", path = "/working/private.txt" }, new { op = "mkdir", path = "/working/new" } } }, true);
        Assert.Equal("workspaces.delete", v.Request.Permission); Assert.Equal(ApprovalKind.ExceptionalGrant, v.Request.Kind);
        var service = WorkspaceEffects(f); var result = await ExecuteApprovedWorkspace(service, v.Frame, v.Request); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome);
        var listing = await WorkspaceReader(f).ListTenantAsync(WorkspaceOwner(v.Setup, true), "/working", null, 100);
        Assert.DoesNotContain(listing.Items, item => item.LogicalPath == "/working/private.txt"); Assert.Contains(listing.Items, item => item.LogicalPath == "/working/new" && item.Directory);
        Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(v.Setup.Tenant, v.Request.RequestId))!.Status);
        Assert.Equal(result, await ExecuteApprovedWorkspace(service, v.Frame, v.Request));
    }
}
