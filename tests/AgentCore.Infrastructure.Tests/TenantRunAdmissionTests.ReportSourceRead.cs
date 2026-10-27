using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_report_creation_authority_cannot_substitute_for_its_private_source_read(bool sqlite, bool review)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var parent = await BackgroundParent(f);
        if (review) await WaitReadAuthority(f, parent);
        var child = await IndependentBackgroundChild(f, parent); var runtime = (ITenantRuntimeStore)f.Iam;
        var claimed = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, claimed.Run, claimed.Snapshot);
        await runtime.CommitTenantOutcomeAsync(claimed.Frame, outcome.Snapshot, claimed.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        await CompleteBackgroundParent(f, parent);
        if (review) await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(parent.Setup.Tenant, parent.Setup.Owner, "agent-runs.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, 0);
        var source = Assert.Single(await runtime.ListTenantBackgroundCompletionsAsync(100)); var report = await PrepareBackgroundReport(f, source);
        var reason = review ? "completion_read_review_required" : "completion_source_read_required";
        Assert.Equal(reason, (await Assert.ThrowsAsync<AccessDeniedException>(() => ReportTarget(f).ResolveAsync(source.Child.Owner, source.Child.AgentRunId, default).AsTask())).ReasonCode);
        Assert.Equal(reason, (await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.AdmitTenantBackgroundCompletionAsync(report, source.Parent!.Snapshot.Revision, f.Ids.NewId()).AsTask())).ReasonCode);
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant, report.AgentRunId));
        Assert.Equal(source.Child.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant, source.Child.AgentRunId))!.Revision);
        Assert.Single(await runtime.ListTenantBackgroundCompletionsAsync(100));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_report_provider_and_atomic_final_save_recheck_current_private_source_read(bool sqlite, bool review)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var parent = await BackgroundParent(f); var read = await WaitReadAuthority(f, parent);
        var child = await IndependentBackgroundChild(f, parent); var runtime = (ITenantRuntimeStore)f.Iam;
        var claimed = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, claimed.Run, claimed.Snapshot);
        await runtime.CommitTenantOutcomeAsync(claimed.Frame, outcome.Snapshot, claimed.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        await CompleteBackgroundParent(f, parent);
        var source = Assert.Single(await runtime.ListTenantBackgroundCompletionsAsync(100)); var report = await PrepareBackgroundReport(f, source);
        await runtime.AdmitTenantBackgroundCompletionAsync(report, source.Parent!.Snapshot.Revision, f.Ids.NewId());
        var authority = new TenantReportReadAuthority(runtime, (ITenantRunStore)f.Iam, f.Iam, f.Authorizer);
        await authority.RequireAsync(report, default);
        var reporting = await ClaimBackground(f, report); var final = BackgroundOutcome(f, reporting.Run, reporting.Snapshot);
        if (review) await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(parent.Setup.Tenant, parent.Setup.Owner, "agent-runs.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, 0);
        else await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, read, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        var reason = review ? "completion_read_review_required" : "completion_source_read_required";
        Assert.Equal(reason, (await Assert.ThrowsAsync<AccessDeniedException>(() => authority.RequireAsync(reporting.Run, default).AsTask())).ReasonCode);
        Assert.Equal(reason, (await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.CommitTenantOutcomeAsync(reporting.Frame, final.Snapshot, reporting.Snapshot.Revision, final.Complete, null, f.Ids.NewId()).AsTask())).ReasonCode);
        Assert.Equal(reporting.Snapshot.Revision, (await runtime.LoadTenantRuntimeAsync(reporting.Frame))!.Revision);
        Assert.Equal(source.Child.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant, source.Child.AgentRunId))!.Revision);
    }
}
