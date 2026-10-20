using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static TenantReportSourceReadService ReportSourceReads(Fixture f) => new((ITenantRuntimeStore)f.Iam, (ITenantRunStore)f.Iam,
        f.Iam, (ITenantApprovalStore)f.Iam, (ITenantApprovedOperationStore)f.Iam, f.RunConfiguration(), f.Authorizer);
    private static async Task<(BackgroundSetup Parent, TenantBackgroundCompletion Source, TenantApprovalRequest Request)> ReportSourceConsent(Fixture f)
    {
        var parent = await BackgroundParent(f); await WaitReadAuthority(f, parent);
        var child = await BackgroundChild(f, parent); var runtime = (ITenantRuntimeStore)f.Iam;
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var claimed = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, claimed.Run, claimed.Snapshot);
        await runtime.CommitTenantOutcomeAsync(claimed.Frame, outcome.Snapshot, claimed.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        await CompleteBackgroundParent(f, parent);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(parent.Setup.Tenant, parent.Setup.Owner, "agent-runs.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, 0);
        var source = Assert.Single(await runtime.ListTenantBackgroundCompletionsAsync(100));
        var prepared = await ReportApprovals(f).PrepareReportSourceReadAsync(source, default);
        return (parent, source, Assert.IsType<TenantApprovalRequest>(prepared.Request));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_report_source_read_Review_settles_without_child_mutation_then_separately_admits_and_delivers_report(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ReportSourceConsent(f);
        var runtime = (ITenantRuntimeStore)f.Iam; var requests = (ITenantApprovalStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var tenant = v.Parent.Setup.Tenant; var service = ReportApprovals(f);
        Assert.Equal(ApprovalKind.Review, v.Request.Kind); Assert.NotNull(v.Request.Operation.ReportSourceRead); Assert.Null(v.Request.Operation.BackgroundReport);
        Assert.Equal(v.Source.Child.AgentRunId, v.Request.RunId); Assert.Equal(v.Source.Child.SessionId, v.Request.SessionId);
        Assert.Empty(await runtime.ListTenantBackgroundCompletionsAsync(1));
        Assert.Equal(v.Request.RequestId, (await service.PrepareReportSourceReadAsync(v.Source, default)).Request!.RequestId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ReportTarget(f).ResolveAsync(v.Source.Child.Owner, v.Source.Child.AgentRunId, default).AsTask());
        var request = await service.DecideAsync(tenant, v.Request.RequestId, v.Parent.Setup.Owner, true, v.Request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, v.Request.Revision);
        var signal = Assert.Single(await requests.PendingApprovalSignalsAsync(100));
        Assert.True(await runtime.SettleTenantReportSourceReadAsync(signal, f.Ids.NewId()));
        Assert.False(await runtime.SettleTenantReportSourceReadAsync(signal, f.Ids.NewId()));
        Assert.Null(await requests.FindOperationGrantAsync(tenant, request.RequestId));
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
        Assert.Equal(TenantReportSourceReadRules.Reason, receipt.ReasonCode); Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
        Assert.Equal(v.Source.Child.Revision, (await runs.FindTenantRunAsync(tenant, v.Source.Child.AgentRunId))!.Revision);
        Assert.Equal(CompletionInboxStatus.Pending, (await runtime.ReadTenantBackgroundCompletionAsync(v.Source.Child.Owner, v.Source.Child.AgentRunId))!.Item!.Status);
        Assert.Equal(OperationDecisionKind.Allowed, (await service.PrepareReportSourceReadAsync(v.Source, default)).Decision);
        var ready = Assert.Single(await runtime.ListTenantBackgroundCompletionsAsync(1));
        var target = await ReportTarget(f).ResolveAsync(ready.Child.Owner, ready.Child.AgentRunId, default);
        var report = await PrepareBackgroundReport(f, target.Source);
        await runtime.AdmitTenantBackgroundCompletionAsync(report, target.Source.Parent!.Snapshot.Revision, f.Ids.NewId());
        await new TenantReportReadAuthority(runtime, runs, f.Iam, f.Authorizer, ReportSourceReads(f)).RequireAsync(report, default);
        var reporting = await ClaimBackground(f, report); var result = BackgroundOutcome(f, reporting.Run, reporting.Snapshot);
        var final = await runtime.CommitTenantOutcomeAsync(reporting.Frame, result.Snapshot, reporting.Snapshot.Revision, result.Complete, null, f.Ids.NewId());
        Assert.Equal(AgentRunStatus.Completed, final.Status);
        Assert.Equal(CompletionInboxStatus.Delivered, (await runtime.ReadTenantBackgroundCompletionAsync(ready.Child.Owner, ready.Child.AgentRunId))!.Item!.Status);
        Assert.Equal(2, (await runs.ListTenantRunIdsAsync(tenant, v.Parent.Setup.Session)).Count);
    }

    [Theory] [InlineData(false, "reject")] [InlineData(true, "reject")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "expired")] [InlineData(true, "expired")] [InlineData(false, "executor")] [InlineData(true, "executor")]
    [InlineData(false, "configuration")] [InlineData(true, "configuration")]
    public async Task Tenant_report_source_read_refusal_or_lost_authority_settles_without_disclosure_report_or_child_mutation(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var v = await ReportSourceConsent(f);
        var runtime = (ITenantRuntimeStore)f.Iam; var requests = (ITenantApprovalStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var tenant = v.Parent.Setup.Tenant; var service = ReportApprovals(f);
        if (mode == "expired") clock.Advance(TimeSpan.FromHours(2));
        else
        {
            if (mode == "cancel") await service.CancelAsync(tenant, v.Request.RequestId, v.Parent.Setup.Owner, v.Request.Operation.ActionHash,
                (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
            else await service.DecideAsync(tenant, v.Request.RequestId, v.Parent.Setup.Owner, mode != "reject", v.Request.Operation.ActionHash,
                (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
            if (mode == "executor")
            {
                var state = (await f.Iam.ReadTenantAsync(tenant))!;
                var role = state.Roles.Single(r => r.Permissions.Contains("agent-runs.read") && !r.IsBuiltin);
                var assignment = state.Assignments.Single(a => a.PrincipalId == v.Parent.Setup.Agent.PrincipalId && a.RoleId == role.RoleId);
                await f.Access.RevokeAssignmentAsync(v.Parent.Setup.Owner.PrincipalId, tenant, assignment.AssignmentId, state.Tenant.Revision);
            }
            if (mode == "configuration")
            {
                var state = (await f.Iam.ReadTenantAsync(tenant))!; var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, v.Parent.Setup.Agent.Instance.InstanceId))!;
                Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                    new(agent.Instance.InstanceId, 1, Persona: agent.Instance.Persona with { Name = "Changed source reader" }, ExpectedPersonaRevision: 1), clock.GetUtcNow(),
                    new(f.Ids.NewId(), tenant, v.Parent.Setup.Owner.PrincipalId, "agents.update", agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", clock.GetUtcNow())));
            }
        }
        Assert.Equal(1, await service.DispatchPendingAsync());
        Assert.Equal(0, await service.DispatchPendingAsync());
        var source = (await runtime.ReadTenantBackgroundCompletionAsync(v.Source.Child.Owner, v.Source.Child.AgentRunId))!;
        Assert.Equal(CompletionInboxStatus.Skipped, source.Item!.Status); Assert.Null(source.Item.ReportActivationId);
        Assert.Equal(v.Source.Child.Revision, (await runs.FindTenantRunAsync(tenant, v.Source.Child.AgentRunId))!.Revision);
        Assert.Single(await runs.ListTenantRunIdsAsync(tenant, v.Parent.Setup.Session));
        var receipt = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, v.Request.Operation.OperationId);
        if (mode is "executor" or "configuration") Assert.Equal(TenantOperationOutcome.Denied, receipt!.Outcome); else Assert.Null(receipt);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ReportTarget(f).ResolveAsync(v.Source.Child.Owner, v.Source.Child.AgentRunId, default).AsTask());
    }

    [Theory] [InlineData(false, "executor")] [InlineData(true, "executor")]
    [InlineData(false, "independence")] [InlineData(true, "independence")]
    [InlineData(false, "reviewer")] [InlineData(true, "reviewer")]
    public async Task Tenant_report_source_read_approved_proof_rechecks_current_authority_at_provider_and_atomic_final_save(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ReportSourceConsent(f);
        var tenant = v.Parent.Setup.Tenant; var owner = v.Parent.Setup.Owner; var service = ReportApprovals(f);
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var reviewer = owner;
        Guid? reviewerAssignment = null;
        if (boundary == "reviewer")
        {
            reviewer = await AttachmentReviewer(f, v.Parent.Setup, ["approvals.read", "approvals.approve", "sessions.read", "agent-runs.read"]);
            reviewerAssignment = (await f.Iam.ReadTenantAsync(tenant))!.Assignments.Single(a => a.PrincipalId == reviewer.PrincipalId).AssignmentId;
        }
        await service.DecideAsync(tenant, v.Request.RequestId, reviewer, true, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        Assert.Equal(1, await service.DispatchPendingAsync());
        var target = await ReportTarget(f).ResolveAsync(v.Source.Child.Owner, v.Source.Child.AgentRunId, default);
        var report = await PrepareBackgroundReport(f, target.Source);
        await runtime.AdmitTenantBackgroundCompletionAsync(report, target.Source.Parent!.Snapshot.Revision, f.Ids.NewId());
        var active = await ClaimBackground(f, report); var final = BackgroundOutcome(f, active.Run, active.Snapshot);
        var authority = new TenantReportReadAuthority(runtime, runs, f.Iam, f.Authorizer, ReportSourceReads(f));
        await authority.RequireAsync(active.Run, default);
        if (boundary == "independence")
            await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
                .SaveAsync(tenant, owner, "agent-runs.read", new(OperationApprovalMode.Always, true), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        else
        {
            var state = (await f.Iam.ReadTenantAsync(tenant))!;
            var assignment = reviewerAssignment ?? state.Assignments.Single(a => a.PrincipalId == v.Parent.Setup.Agent.PrincipalId
                && state.Roles.Single(r => r.RoleId == a.RoleId).Permissions.Contains("agent-runs.read")).AssignmentId;
            await f.Access.RevokeAssignmentAsync(owner.PrincipalId, tenant, assignment, state.Tenant.Revision);
        }
        await Assert.ThrowsAsync<AccessDeniedException>(() => authority.RequireAsync(active.Run, default).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.CommitTenantOutcomeAsync(active.Frame, final.Snapshot, active.Snapshot.Revision, final.Complete, null, f.Ids.NewId()).AsTask());
        Assert.Equal(active.Snapshot.Revision, (await runtime.LoadTenantRuntimeAsync(active.Frame))!.Revision);
        Assert.Equal(active.Run.Revision, (await runs.FindTenantRunAsync(tenant, active.Run.AgentRunId))!.Revision);
        var source = (await runtime.ReadTenantBackgroundCompletionAsync(v.Source.Child.Owner, v.Source.Child.AgentRunId))!;
        Assert.Equal(CompletionInboxStatus.DeliveryQueued, source.Item!.Status);
        Assert.Equal(v.Source.Child.Revision, source.Child.Revision);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_report_source_read_visibility_requires_exact_raw_source_and_destination_before_count_and_page(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ReportSourceConsent(f);
        var tenant = v.Parent.Setup.Tenant; var owner = v.Parent.Setup.Owner; var member = await Member(f, tenant, owner.PrincipalId);
        async Task Grant(string name, string[] keys, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(owner.PrincipalId, tenant, null, name, keys, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
            await f.Access.AssignRoleAsync(owner.PrincipalId, tenant, member.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        }
        var service = ReportApprovals(f);
        await Grant("Read request inventory", ["approvals.read"], AccessScope.ForTenant(tenant));
        await Grant("Read exact child", ["sessions.read"], TenantSessionRules.Scope(tenant, v.Source.Child.SessionId));
        await Grant("Read exact result", ["agent-runs.read"], TenantRunRules.Scope(tenant, v.Source.Child.AgentRunId));
        Assert.Equal(0, (await service.ListAsync(tenant, member, new(PageSize: 1))).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadViewAsync(tenant, v.Request.RequestId, member).AsTask());
        await Grant("Read destination", ["sessions.read"], TenantSessionRules.Scope(tenant, v.Parent.Parent.SessionId));
        var page = await service.ListAsync(tenant, member, new(PageSize: 1)); Assert.Equal(1, page.Total); Assert.Single(page.Items);
        Assert.False((await service.ReadViewAsync(tenant, v.Request.RequestId, member)).Review!.CanDecide);
        Assert.Empty((await service.ListAsync(tenant, member, new(1, 1))).Items);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_report_source_read_pending_consent_does_not_hide_other_ready_completions_before_limit(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var blocked = await ReportSourceConsent(f);
        var runtime = (ITenantRuntimeStore)f.Iam; var tenant = blocked.Parent.Setup.Tenant; var owner = blocked.Parent.Setup.Owner;
        var session = await f.Sessions.CreateAsync(tenant, blocked.Parent.Parent.AgentInstanceId, owner, SessionMode.Text, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        var admitted = await f.RunService().SubmitTextAsync(tenant, session.Owner.SessionId, owner, f.Ids.NewId(), "Another ready report", (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        var live = await ClaimBackground(f, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, admitted.RunId))!);
        var readyParent = blocked.Parent with { Setup = blocked.Parent.Setup with { Session = session.Owner.SessionId }, Parent = live.Run, Frame = live.Frame, Snapshot = live.Snapshot };
        var child = await BackgroundChild(f, readyParent);
        await runtime.AdmitTenantImmediateAsync(readyParent.Frame, child.Session, child.Run, f.Ids.NewId());
        var claimed = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, claimed.Run, claimed.Snapshot);
        await runtime.CommitTenantOutcomeAsync(claimed.Frame, outcome.Snapshot, claimed.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        await CompleteBackgroundParent(f, readyParent);
        Assert.Equal(child.Run.AgentRunId, Assert.Single(await runtime.ListTenantBackgroundCompletionsAsync(1)).Child.AgentRunId);
        Assert.Equal(TenantApprovalStatus.Pending, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(blocked.Request.TenantId, blocked.Request.RequestId))!.Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_report_source_read_binds_terminal_child_approval_successor_and_retains_initial_source_identity(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var parent = await BackgroundParent(f); await WaitReadAuthority(f, parent);
        var child = await BackgroundChild(f, parent); var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var tenant = parent.Setup.Tenant;
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var claimed = await ClaimBackground(f, child.Run); var approvals = ReportApprovals(f);
        var persona = parent.Setup.Agent.Instance.Persona with { Name = "Never applied" };
        var call = new ModelToolCall("earlier-child-consent", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "update_persona",
            persona = new { name = persona.Name, role = persona.Role, description = persona.Description, tone = persona.Tone } }));
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 100000);
        var checkpointed = await runtime.ApplyTenantRuntimeCommandAsync(claimed.Run.Owner, claimed.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(claimed.Run.Revision, f.Clock.GetUtcNow(), claimed.Frame.Generation, checkpoint, null), f.Ids.NewId());
        var earlier = (await approvals.PreparePersonaAsync(new(tenant, checkpointed.AgentRunId), persona, checkpointed.Revision, claimed.Frame.Generation)).Request!;
        Assert.NotNull(earlier);
        await approvals.DecideAsync(tenant, earlier.RequestId, parent.Setup.Owner, false, earlier.Operation.ActionHash, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        Assert.Equal(1, await approvals.DispatchPendingAsync());
        var initial = (await runs.FindTenantRunAsync(tenant, child.Run.AgentRunId))!; Assert.Equal(AgentRunOutcomeKind.Continued, initial.Result!.OutcomeKind);
        var active = await ClaimBackground(f, (await runs.FindTenantRunAsync(tenant, initial.Result.ContinuationRunId!.Value))!);
        var resumed = await new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, CompletionReadEffects(f), f.Clock, runtime, f.Ids).ResumeAsync(active.Run);
        Assert.Equal(TenantOperationOutcome.Denied, resumed.Outcome!.Outcome);
        active = (resumed.Run, active.Frame, (await runtime.LoadTenantRuntimeAsync(active.Frame))!);
        var final = BackgroundOutcome(f, active.Run, active.Snapshot);
        await runtime.CommitTenantOutcomeAsync(active.Frame, final.Snapshot, active.Snapshot.Revision, final.Complete, null, f.Ids.NewId());
        await CompleteBackgroundParent(f, parent);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(tenant, parent.Setup.Owner, "agent-runs.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 0);
        var source = Assert.Single(await runtime.ListTenantBackgroundCompletionsAsync(100)); Assert.Equal(child.Run.AgentRunId, source.InitialChild!.AgentRunId);
        Assert.Equal(active.Run.AgentRunId, source.Child.AgentRunId);
        var request = (await approvals.PrepareReportSourceReadAsync(source, default)).Request!; Assert.NotNull(request);
        Assert.Equal(initial.AgentRunId, request.Operation.ReportSourceRead!.InitialRunId); Assert.Equal(active.Run.AgentRunId, request.Operation.ReportSourceRead.SourceRunId);
        await approvals.DecideAsync(tenant, request.RequestId, parent.Setup.Owner, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        Assert.Equal(1, await approvals.DispatchPendingAsync());
        var target = await ReportTarget(f).ResolveAsync(source.Child.Owner, source.Child.AgentRunId, default); var report = await PrepareBackgroundReport(f, target.Source);
        await runtime.AdmitTenantBackgroundCompletionAsync(report, target.Source.Parent!.Snapshot.Revision, f.Ids.NewId());
        await new TenantReportReadAuthority(runtime, runs, f.Iam, f.Authorizer, ReportSourceReads(f)).RequireAsync(report, default);
    }

    [Fact]
    public async Task Tenant_report_source_read_SQLite_preserves_guards_and_refuses_mutation_and_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        await using var db = f.Contexts!.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261020130000_TenantCompletionReadReview"); await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(13, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Tenant%'").SingleAsync());
        var v = await ReportSourceConsent(f); var id = v.Request.RequestId;
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson, '$.Operation.ReportSourceRead.SourceRevision', 999) WHERE RequestId={id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020130000_TenantCompletionReadReview"));
        Assert.Equal(v.Request.Operation, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(v.Request.TenantId, id))!.Operation);
    }
}
