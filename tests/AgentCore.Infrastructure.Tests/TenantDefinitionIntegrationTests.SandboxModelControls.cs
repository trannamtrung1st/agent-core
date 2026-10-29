using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static (TenantApprovalService Approvals, TenantApprovedOperationService Effects) SandboxControlServices(Fixture f, RuntimeSandboxSetup s, IIamStore? current = null)
    {
        current ??= f.Iam; var authorizer = new IamAuthorizer(current);
        var operations = new TenantOperationAuthorizer(current, (ITenantRunStore)current, (ITenantAgentStore)current, authorizer,
            new((ITenantAuthorizationAuditStore)current, f.Ids, TimeProvider.System));
        var target = new TenantSandboxControlOperationTarget((ITenantSessionStore)current, (ITenantSandboxRuntimeStore)current, s.Resolver, current, authorizer);
        return (new(current, (ITenantApprovalStore)current, (ITenantAgentStore)current, operations, authorizer, f.Identity, f.Ids, TimeProvider.System,
                s.Resolver, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(current), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)current, controlTargets: target),
            new(current, (ITenantRunStore)current, (ITenantAgentStore)current, (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current,
                operations, s.Resolver, f.Ids, TimeProvider.System, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(current), SyntheticProviderAliases.Default, target));
    }
    private static async Task GrantModelControl(Fixture f, RuntimeSandboxSetup s)
    {
        await GrantSandboxRead(f, s, "sessions.read", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await GrantSandboxRead(f, s, "sessions.manage", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await GrantSandboxRead(f, s, "sandbox.read", AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
        await GrantSandboxRead(f, s, "sandbox.manage", AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_model_control_reset_requires_exact_Review_records_only_consent_and_reopens_idempotently(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, controls: true); await ReadySandbox(f, s);
        await GrantModelControl(f, s); var services = SandboxControlServices(f, s); var store = (ITenantSandboxRuntimeStore)f.Iam;
        var prepared = await services.Approvals.PrepareSandboxControlAsync(new(s.Tenant, s.Run.AgentRunId), SandboxControlKind.ResetEnvironment, null, s.Run.Revision, s.Run.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, prepared.Decision); var request = prepared.Request!;
        Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("sandbox.manage", request.Permission); Assert.Null(request.Operation.SandboxCommand);
        Assert.Equal(s.Lease.Generation, request.Operation.SandboxControl!.Generation);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET Kind=1 WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET Permission='sandbox.execute' WHERE RequestId={request.RequestId}"));
            await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check";
            using var reader = await command.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
        }
        var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        Assert.Equal(SandboxLeaseState.Ready, saved.Lease!.State); Assert.Null(saved.Lease.CleanupReason);
        // A factual observation revision changes while the protected target remains the same.
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, saved.Lease with { Revision = saved.Lease.Revision + 1, SafeMessage = "Observed Ready" }, saved.Lease.Revision));
        var correlated = TenantApprovalRules.ForRun(TenantApprovalRules.PrepareSandboxControl(f.Ids.NewId(), request.Operation.InstanceId,
            request.Operation.SandboxControl with { ObservedLeaseRevision = saved.Lease.Revision + 1 }, request.Operation.ExpectedInstanceRevision, request.Operation.ExpectedPersonaRevision), s.Run);
        var approvals = (ITenantApprovalStore)f.Iam;
        Assert.Equal(request.RequestId, (await approvals.FindRootApprovalAsync(s.Tenant, request.RootActivationId, request.ExecutorPrincipalId, correlated))!.RequestId);
        var changed = correlated with { SandboxControl = correlated.SandboxControl! with { Generation = f.Ids.NewId() } };
        Assert.Null(await approvals.FindRootApprovalAsync(s.Tenant, request.RootActivationId, request.ExecutorPrincipalId, changed));
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
        Assert.True(view.Review!.CanDecide); Assert.Contains(view.Review.Changes, c => c.Field == "cleanupConsequence");
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        if (sqlite) services = SandboxControlServices(f, s, new SqliteIamStore(f.Contexts!, f.Content));
        var result = await services.Effects.ExecuteAsync(new(s.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, result.Outcome); Assert.Equal("sandbox_reset_requested", result.ReasonCode);
        var audit = Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantSandboxControlOperationRules.AuditAction);
        Assert.Equal("sandbox.manage", audit.Execution!.Permission); Assert.Equal(s.Session.ExecutorPrincipalId, audit.ActorPrincipalId);
        Assert.Equal(request.RequestId, audit.Execution.ApprovalRequestId);
        var pending = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        Assert.Equal(SandboxLeaseState.Terminating, pending.Lease!.State); Assert.Equal(s.Lease.Generation, pending.Lease.Generation);
        Assert.Equal("reset", pending.Lease.CleanupReason); Assert.Null(pending.Lease.SelectedProfileId); Assert.Null(pending.Lease.SelectionWitness);
        Assert.Equal(result, await services.Effects.ExecuteAsync(new(s.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash));
        Assert.Equal(pending.Lease, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
        Assert.Null(await f.Sandbox.GetLeaseAsync(s.Session.SessionId));
    }
    [Theory] [InlineData(false, "approve")] [InlineData(true, "approve")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")] [InlineData(false, "replaced")] [InlineData(true, "replaced")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    public async Task Tenant_Sandbox_model_control_Stop_binds_exact_process_progress_and_current_authority(bool sqlite, string mode)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, controls: true); await ReadySandbox(f, s);
        var store = (ITenantSandboxRuntimeStore)f.Iam; var execution = RuntimeExecution(f, s) with { Background = true };
        Assert.True(await store.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
        execution = execution with { Revision = 2, State = SandboxExecutionState.Running, Phase = "execution", ProviderCommandId = "PRIVATE_COMMAND" };
        Assert.True(await store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution, 1));
        await GrantModelControl(f, s); var services = SandboxControlServices(f, s);
        var request = (await services.Approvals.PrepareSandboxControlAsync(new(s.Tenant, s.Run.AgentRunId), SandboxControlKind.StopProcess, execution.Id, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        execution = execution with { Revision = 3, Stdout = "factual progress during human wait" };
        Assert.True(await store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution, 2));
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
        Assert.True(view.Review!.CanDecide); Assert.DoesNotContain(view.Review.Changes, c => c.Field == "command");
        if (mode == "cancel") await services.Approvals.CancelAsync(s.Tenant, request.RequestId, SandboxHuman(s), request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        else await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), mode != "reject", request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        if (mode is "cancel" or "reject")
        {
            Assert.Null((await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution!.ProcessStop);
            Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId)); return;
        }
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        if (mode == "replaced")
        {
            var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
            await new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer, f.Ids, TimeProvider.System)
                .RequestResetAsync(s.Tenant, s.Human, new(s.Session.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision, execution.Id), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        }
        if (mode == "revoked")
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
            var role = Assert.Single(state.Roles, r => r.Name == "Exact model sessions.manage");
            var assignment = Assert.Single(state.Assignments, a => a.PrincipalId == s.Session.ExecutorPrincipalId && a.RoleId == role.RoleId);
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
        }
        var result = await services.Effects.ExecuteAsync(new(s.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash);
        var after = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        Assert.Equal(execution.Stdout, after.Execution!.Stdout); Assert.Equal(execution.CommandText, after.Execution.CommandText);
        if (mode != "approve") { Assert.Equal(TenantOperationOutcome.Denied, result.Outcome); Assert.Null(after.Execution.ProcessStop); return; }
        Assert.Equal("sandbox_stop_requested", result.ReasonCode); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome);
        Assert.Equal(request.RequestId, after.Execution.ProcessStop!.RequestId); Assert.Equal(s.Human.PrincipalId, after.Execution.ProcessStop.PrincipalId);
        Assert.Null(after.Execution.ProcessStop.DispatchStartedAt); Assert.False(after.Execution.TerminationConfirmed);
        Assert.Equal(result, await services.Effects.ExecuteAsync(new(s.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash));
        Assert.Equal(after.Execution, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_model_control_cannot_expand_saved_capability_but_can_retire_disabled_sources_without_execute_permission(bool sqlite, bool controls)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, controls: controls); await ReadySandbox(f, s); await GrantModelControl(f, s);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, state.Tenant.Revision);
        var p = SetupProfiles(f); var latest = (await p.Profiles.ReadAsync(s.Tenant, s.Lease.Profile.Id, s.Human)).Profile;
        await p.Profiles.SaveAsync(s.Tenant, latest.Id, s.Human, SetupInput(latest.Name) with { Enabled = false }, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, latest.Revision);
        var services = SandboxControlServices(f, s);
        if (!controls)
        {
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => services.Approvals.PrepareSandboxControlAsync(new(s.Tenant, s.Run.AgentRunId), SandboxControlKind.ResetEnvironment, null, s.Run.Revision, s.Run.Claim!.Generation).AsTask());
            Assert.Equal("sandbox_policy_changed", denied.ReasonCode); return;
        }
        var request = (await services.Approvals.PrepareSandboxControlAsync(new(s.Tenant, s.Run.AgentRunId), SandboxControlKind.ResetEnvironment, null, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s)); Assert.True(view.Review!.CanDecide);
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        var result = await services.Effects.ExecuteAsync(new(s.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, result.Outcome);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_model_control_needs_secondary_scopes_and_a_qualified_independent_reviewer(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, controls: true); await ReadySandbox(f, s);
        var services = SandboxControlServices(f, s);
        await Assert.ThrowsAsync<AccessDeniedException>(() => services.Approvals.PrepareSandboxControlAsync(new(s.Tenant, s.Run.AgentRunId), SandboxControlKind.ResetEnvironment, null, s.Run.Revision, s.Run.Claim!.Generation).AsTask());
        await GrantModelControl(f, s);
        var policy = new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System);
        await policy.SaveAsync(s.Tenant, SandboxHuman(s), "sandbox.manage", new(OperationApprovalMode.Always, true), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var denied = await services.Approvals.PrepareSandboxControlAsync(new(s.Tenant, s.Run.AgentRunId), SandboxControlKind.ResetEnvironment, null, s.Run.Revision, s.Run.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.Denied, denied.Decision); Assert.Equal("no_eligible_approver", denied.ReasonCode); Assert.Null(denied.Request);
        Assert.Equal(SandboxLeaseState.Ready, (await ((ITenantSandboxRuntimeStore)f.Iam).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!.State);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_model_control_decision_transaction_rechecks_target_and_cancel_can_release_a_stale_wait(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, controls: true); await ReadySandbox(f, s); await GrantModelControl(f, s);
        var service = SandboxControlServices(f, s).Approvals;
        var request = (await service.PrepareSandboxControlAsync(new(s.Tenant, s.Run.AgentRunId), SandboxControlKind.ResetEnvironment, null, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var store = (ITenantSandboxRuntimeStore)f.Iam; var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        await new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer, f.Ids, TimeProvider.System)
            .RequestResetAsync(s.Tenant, s.Human, new(s.Session.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision, null), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var approvals = (ITenantApprovalStore)f.Iam;
        async Task<bool> Decide(TenantApprovalStatus outcome)
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var at = DateTimeOffset.UtcNow;
            var next = request.Decide(outcome, request.Revision, at);
            return await approvals.TryDecideApprovalAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                next, request.Revision, new(f.Ids.NewId(), request.RequestId, s.Tenant, s.Human.PrincipalId, outcome, request.Operation.ActionHash, next.Revision, at), null,
                new(f.Ids.NewId(), s.Tenant, request.RequestId, request.RunId, next.Revision, 1, at, null), SandboxHuman(s),
                new(f.Ids.NewId(), s.Tenant, s.Human.PrincipalId, "approvals.decide", request.RequestId.ToString("D"), "allowed", outcome == TenantApprovalStatus.Cancelled ? "approval_cancelled" : "approval_approved", at));
        }
        var error = await Assert.ThrowsAsync<SecurityConflictException>(() => Decide(TenantApprovalStatus.Approved)); Assert.Equal("sandbox_operation_witness_changed", error.ReasonCode);
        Assert.Equal(TenantApprovalStatus.Pending, (await approvals.FindApprovalAsync(s.Tenant, request.RequestId))!.Status);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(s.Tenant, request.RequestId));
        Assert.True(await Decide(TenantApprovalStatus.Cancelled)); Assert.Equal(TenantApprovalStatus.Cancelled, (await approvals.FindApprovalAsync(s.Tenant, request.RequestId))!.Status);
    }

}
