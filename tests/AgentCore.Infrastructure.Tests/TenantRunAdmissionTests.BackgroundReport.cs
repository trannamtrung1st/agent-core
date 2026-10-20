using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static TenantBackgroundReportTarget ReportTarget(Fixture f) => new((ITenantRuntimeStore)f.Iam, (ITenantRunStore)f.Iam, f.Iam, f.RunConfiguration(), f.Authorizer, ModelCatalogFactory.Synthetic(), ReportSourceReads(f));
    private static TenantApprovalService ReportApprovals(Fixture f) => new(f.Iam, (ITenantApprovalStore)f.Iam, (ITenantAgentStore)f.Iam, AttachmentOperations(f), f.Authorizer,
        f.Identity, f.Ids, f.Clock, f.RunConfiguration(), ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)f.Iam,
        backgroundReports: ReportTarget(f), reportRuntime: (ITenantRuntimeStore)f.Iam, reportSourceReads: ReportSourceReads(f));
    private static async Task<(BackgroundSetup Parent, TenantBackgroundCompletion Source, TenantApprovalRequest Request)> CompletedReportCase(Fixture f)
    {
        var parent = await BackgroundParent(f); await WaitReadAuthority(f, parent); var child = await BackgroundChild(f, parent); var runtime = (ITenantRuntimeStore)f.Iam;
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var claimed = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, claimed.Run, claimed.Snapshot);
        await runtime.CommitTenantOutcomeAsync(claimed.Frame, outcome.Snapshot, claimed.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        await CompleteBackgroundParent(f, parent);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(parent.Setup.Tenant, parent.Setup.Owner, "agent-runs.create", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, 0);
        var source = Assert.Single(await runtime.ListTenantBackgroundCompletionsAsync(100));
        var prepared = await ReportApprovals(f).PrepareBackgroundReportAsync(source, default); Assert.NotNull(prepared.Request);
        return (parent, source, prepared.Request);
    }
    [Theory] [InlineData(false, "replay")] [InlineData(true, "replay")]
    [InlineData(false, "executor")] [InlineData(true, "executor")]
    [InlineData(false, "configuration")] [InlineData(true, "configuration")]
    [InlineData(false, "forged")] [InlineData(true, "forged")]
    [InlineData(false, "expired")] [InlineData(true, "expired")]
    public async Task Tenant_report_creation_store_rechecks_exact_source_authority_and_receipt_replay_without_mutating_finished_child(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T01:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var v = await CompletedReportCase(f); var tenant = v.Parent.Setup.Tenant;
        var approvals = ReportApprovals(f); var requests = (ITenantApprovalStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var request = await approvals.DecideAsync(tenant, v.Request.RequestId, v.Parent.Setup.Owner, true, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        var signal = Assert.Single(await requests.PendingApprovalSignalsAsync(100));
        var current = await ReportTarget(f).RequireAsync(request, default); var report = TenantBackgroundReportRules.Build(request, current.Source, current.Configuration);
        if (boundary == "executor") await f.Access.RevokeAssignmentAsync(v.Parent.Setup.Owner.PrincipalId, tenant, v.Parent.Assignment!.Value, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        if (boundary == "configuration")
        {
            var state = (await f.Iam.ReadTenantAsync(tenant))!; var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, report.AgentInstanceId))!;
            Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                new(agent.Instance.InstanceId, 1, Persona: agent.Instance.Persona with { Name = "Changed report persona" }, ExpectedPersonaRevision: 1), f.Clock.GetUtcNow(),
                new(f.Ids.NewId(), tenant, v.Parent.Setup.Owner.PrincipalId, "agents.update", agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", f.Clock.GetUtcNow())));
        }
        if (boundary == "forged") report = TenantBackgroundCompletionFactory.Create(current.Source, current.Configuration, current.Source.Parent!.Snapshot.ModelSelection!, report.ActivationId, f.Ids.NewId(), report.ResponseId!.Value, request.CreatedAt);
        if (boundary == "expired") clock.Advance(TimeSpan.FromHours(2));
        var revision = (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        if (boundary == "forged")
        {
            Assert.Equal("operation_mismatch", (await Assert.ThrowsAsync<SecurityConflictException>(() => runtime.SettleTenantBackgroundReportApprovalAsync(signal, report, null, f.Ids.NewId()).AsTask())).ReasonCode);
            Assert.Equal(revision, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision); Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, request.Operation.OperationId));
        }
        else
        {
            Assert.True(await runtime.SettleTenantBackgroundReportApprovalAsync(signal, report, null, f.Ids.NewId()));
            Assert.False(await runtime.SettleTenantBackgroundReportApprovalAsync(signal, report, null, f.Ids.NewId()));
            var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
            Assert.Equal(boundary == "replay" ? TenantOperationOutcome.Completed : TenantOperationOutcome.Denied, receipt.Outcome);
            Assert.Equal(revision + 1, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        }
        Assert.Equal(v.Source.Child.Revision, (await runs.FindTenantRunAsync(tenant, v.Source.Child.AgentRunId))!.Revision);
        var created = await runs.FindTenantRunAsync(tenant, request.Operation.BackgroundReport!.RunId);
        if (boundary == "replay") Assert.NotNull(created); else Assert.Null(created);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_report_creation_filters_source_reads_before_totals_and_rechecks_reviewer_after_decision(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await CompletedReportCase(f); var tenant = v.Parent.Setup.Tenant; var owner = v.Parent.Setup.Owner;
        var member = await Member(f, tenant, owner.PrincipalId); var service = ReportApprovals(f);
        async Task<RoleAssignment> Grant(string name, string[] permissions, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(owner.PrincipalId, tenant, null, name, permissions, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
            return await f.Access.AssignRoleAsync(owner.PrincipalId, tenant, member.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        }
        await Grant("Report reader", ["agent-runs.create", "sessions.read", "approvals.read"], AccessScope.ForTenant(tenant));
        Assert.Equal(0, (await service.ListAsync(tenant, member, new(PageSize: 1))).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadViewAsync(tenant, v.Request.RequestId, member).AsTask());
        var sourceRead = await Grant("Exact completed Run reader", ["agent-runs.read"], TenantRunRules.Scope(tenant, v.Source.Child.AgentRunId));
        Assert.Equal(1, (await service.ListAsync(tenant, member, new(PageSize: 1))).Total);
        Assert.False((await service.ReadViewAsync(tenant, v.Request.RequestId, member)).Review!.CanDecide);
        await Grant("Report reviewer", ["approvals.approve"], AccessScope.ForTenant(tenant));
        var request = await service.DecideAsync(tenant, v.Request.RequestId, member, true, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        var current = await ReportTarget(f).RequireAsync(request, default); var report = TenantBackgroundReportRules.Build(request, current.Source, current.Configuration);
        var signal = Assert.Single(await ((ITenantApprovalStore)f.Iam).PendingApprovalSignalsAsync(100));
        await f.Access.RevokeAssignmentAsync(owner.PrincipalId, tenant, sourceRead.AssignmentId, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        Assert.True(await ((ITenantRuntimeStore)f.Iam).SettleTenantBackgroundReportApprovalAsync(signal, report, null, f.Ids.NewId()));
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
        Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome); Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, report.AgentRunId));
        Assert.Equal(v.Source.Child.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, v.Source.Child.AgentRunId))!.Revision);
    }
    [Fact]
    public async Task Tenant_report_creation_SQLite_preserves_prior_guards_and_refuses_immutable_payload_change_and_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var v = await CompletedReportCase(f);
        await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
        var guards = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Tenant%'").ToArrayAsync();
        Assert.Contains("TR_TenantApprovals_BackgroundReport_Insert", guards); Assert.Contains("TR_TenantApprovals_BackgroundReport_Update", guards); Assert.Contains("TR_TenantApprovals_BackgroundCreation_Update", guards);
        Assert.Contains("TR_TenantApprovals_BrowserArtifacts_Insert", guards); Assert.Contains("TR_TenantReceipts_BrowserArtifacts_Insert", guards); Assert.Contains("TR_TenantApprovals_BrowserFiles_Insert", guards);
        var id = v.Request.RequestId;
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson, '$.Operation.BackgroundReport.ParentRevision', 999) WHERE RequestId={id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020110000_TenantBackgroundCreationReview"));
        Assert.Equal(v.Request.Operation, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(v.Request.TenantId, id))!.Operation);
    }
}
