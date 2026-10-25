using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static async Task<(Environment Setup, TenantRuntimeFrame Frame, TenantApprovalRequest Request)> ApprovedWorkspaceCase(Fixture f, bool working, bool exceptional)
    {
        var v = await MutationReviewFixture(f, working, exceptional); var s = v.Setup;
        var pending = await v.Approvals.ListAsync(s.Tenant, s.Owner, new());
        Assert.Contains(pending.Items, item => item.Request.RequestId == v.Request.RequestId);
        var request = await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, true, v.Request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, v.Request.Revision);
        Assert.Equal(1, await v.Approvals.DispatchPendingAsync());
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        return (s, new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), request);
    }
    private static ValueTask<TenantOperationReceipt> ExecuteApprovedWorkspace(TenantWorkspaceMutationEffectService service, TenantRuntimeFrame frame,
        TenantApprovalRequest request, CancellationToken ct = default) => service.ExecuteApprovedAsync(new(frame.Owner.TenantId!.Value, frame.RunId), request.RequestId,
            request.Operation.OperationId, frame.Generation, request.Operation.ActionHash, ct);

    [Theory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_approved_workspace_effect_writes_exact_saved_literal_once_and_consumes_only_its_exact_grant(bool sqlite, bool working, bool exceptional)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ApprovedWorkspaceCase(f, working, exceptional); var s = v.Setup;
        var calls = 0; var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; }));
        var receipt = await ExecuteApprovedWorkspace(service, v.Frame, v.Request);
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.Equal(TenantWorkspaceApprovedEffectRules.Completed, receipt.ReasonCode);
        Assert.Equal(v.Request.RequestId, receipt.RequestId); Assert.Null(receipt.SourceCallId); Assert.Null(receipt.ParametersHash);
        Assert.Equal(receipt, await ExecuteApprovedWorkspace(service, v.Frame, v.Request)); Assert.Equal(1, calls);
        Assert.DoesNotContain("Exact reviewed literal", JsonSerializer.Serialize(receipt));
        var file = await WorkspaceReader(f).InspectTenantAsync(WorkspaceOwner(s, working), (working ? "/working/" : "/home/") + "private.txt", 1024);
        Assert.Equal("Exact reviewed literal replacement", Encoding.UTF8.GetString(file.Bytes));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        Assert.Equal(AgentRunSideEffectDisposition.Succeeded, run.SideEffect.Disposition); Assert.Null(run.SideEffect.ExternalEffect!.DirectOperation);
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId);
        Assert.Equal(exceptional, grant is not null); if (grant is not null) Assert.Equal(OperationGrantStatus.Consumed, grant.Status);
        var audit = await f.Iam.ReadAuditAsync(s.Tenant, 200);
        Assert.Single(audit, a => a.Action == TenantWorkspaceEffectRules.Dispatch); Assert.Single(audit, a => a.Action == TenantWorkspaceEffectRules.Result);
        Assert.Equal(0, await service.RecoverAsync());
    }
}
