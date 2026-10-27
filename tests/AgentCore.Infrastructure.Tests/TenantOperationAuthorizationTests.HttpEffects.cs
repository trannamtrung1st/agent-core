using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture=AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantPublicWebOperationTarget HttpTargets(Fixture f)=>new(Operations(f),(ITenantRunStore)f.Iam,(ITenantSessionStore)f.Iam,f.RunConfiguration(),f.Authorizer,f.Iam,f.Clock,ToolConfigurationGates.AllowAll);
    private static TenantHttpEffectService HttpEffects(Fixture f,IHttpRequestClient http,IIamStore? store=null)
    {
        var current=store ?? f.Iam;
        return new(current,(ITenantRunStore)current,(ITenantApprovalStore)current,(ITenantApprovedOperationStore)current,(ITenantHttpOperationStore)current,HttpTargets(f),new(current),Operations(f),http,f.Ids,f.Clock);
    }
    private static async Task<(Environment Setup,TenantRuntimeFrame Frame,TenantApprovalRequest Request)> ApprovedHttp(Fixture f,bool exceptional=false)
    {
        var s=await Setup(f,"general-assistant",35);var frame=await ClaimArtifactRun(f,s);
        await GrantWorkspaceRuntimeReads(f,s,exceptional?["agents.read","sessions.read"]:["agents.read","sessions.read","http.request"]);
        const string args="{\"method\":\"POST\",\"url\":\"https://public.example/submit\",\"body\":\"exact HTTP proposal\"}";
        var call=new ModelToolCall("exact-http",ToolCatalog.RequestAuthorization,"{\"operation\":\"send_http_request\",\"request\":"+args+"}");
        var run=await WorkspaceEffectCheckpoint(f,frame,call);var service=ApprovalService(f,webTargets:HttpTargets(f));
        var request=(await service.PrepareHttpRequestAsync(new(s.Tenant,run.AgentRunId),args,run.Revision,frame.Generation)).Request!;Assert.NotNull(request);
        request=await service.DecideAsync(s.Tenant,request.RequestId,s.Owner,true,request.Operation.ActionHash,(await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision,request.Revision);
        Assert.Equal(1,await service.DispatchPendingAsync());run=(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant,run.AgentRunId))!;
        run=(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant,run.Result!.ContinuationRunId!.Value))!;
        run=await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner,run.AgentRunId,new AgentRunCommand.Claim(run.Revision,f.Clock.GetUtcNow(),f.Ids.NewId(),f.Clock.GetUtcNow().AddMinutes(5)),f.Ids.NewId());
        return(s,new(run.Owner,run.SessionId,run.AgentRunId,run.Claim!.Generation),request);
    }
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task Tenant_http_completed_checkpoint_reopens_without_network_and_fresh_source_loss_blocks_body(bool sqlite,bool exceptional)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var v=await ApprovedHttp(f,exceptional);var native=new OwnedHttpPort();var service=HttpEffects(f,native);
        var approvals=(ITenantApprovalStore)f.Iam;
        Assert.Equal(0,(await approvals.ListApprovalsAsync(v.Setup.Tenant,null,null,new())).Total);
        Assert.Equal(0,(await approvals.ListApprovalsAsync(v.Setup.Tenant,null,null,new(),httpVisibility:new([],null))).Total);
        Assert.Equal(0,(await approvals.ListApprovalsAsync(v.Setup.Tenant,null,null,new(),httpVisibility:new(null,[]))).Total);
        Assert.Equal(1,(await approvals.ListApprovalsAsync(v.Setup.Tenant,null,null,new(),httpVisibility:new([v.Setup.Agent.Instance.InstanceId],[v.Frame.SessionId]))).Total);
        var receipt=await service.ExecuteApprovedAsync(new(v.Setup.Tenant,v.Frame.RunId),v.Request.RequestId,v.Request.Operation.OperationId,v.Frame.Generation,v.Request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed,receipt.Outcome);Assert.NotNull(receipt.Http);Assert.Equal(1,native.Calls);
        var current=sqlite?new SqliteIamStore(f.Contexts!,builtIns:f.BuiltIns,clock:f.Clock):f.Iam;
        var run=(await ((ITenantRunStore)current).FindTenantRunAsync(v.Setup.Tenant,v.Frame.RunId))!;Assert.Single(TenantHttpReferences.Read(run));
        await HttpEffects(f,native,current).RequireStoredAsync(run);
        if(sqlite)
        {
            await using var db=f.Contexts!.CreateDbContext();
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>db.Database.ExecuteSqlRawAsync("UPDATE TenantOperationReceipts SET HttpJson=NULL WHERE OperationId={0}",receipt.OperationId));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>db.Database.ExecuteSqlRawAsync("DELETE FROM TenantOperationReceipts WHERE OperationId={0}",receipt.OperationId));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>db.GetService<IMigrator>().MigrateAsync("20261023180000_TenantPublicDownloads"));
        }
        Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint,out var savedMessages));
        var original=savedMessages!.Single(m=>m.Role==ModelRole.Tool && m.Name==ToolCatalog.RequestAuthorization);
        var changed=System.Text.Json.Nodes.JsonNode.Parse(original.Text)!;changed["response"]!["extra"]="UNOWNED-CHECKPOINT-BODY";
        var corrupt=run.SaveCheckpoint(run.Revision,v.Frame.Generation,new(AgentRunToolCallCheckpoint.Write(savedMessages!.Select(m=>m==original?m with { Text=changed.ToJsonString() }:m).ToArray()),run.Checkpoint!.StepCount,run.Checkpoint.OutputBytes,run.Checkpoint.RemainingOverallBudgetMs,run.Checkpoint.ActiveExecutionMs,run.Checkpoint.BudgetRecordedAtUtc,run.Checkpoint.AwaitingRuntimeAdmission),null,f.Clock.GetUtcNow());
        Assert.Throws<AccessDeniedException>(()=>TenantHttpReferences.Read(corrupt));
        Assert.Equal(receipt,await HttpEffects(f,native,current).ExecuteApprovedAsync(new(v.Setup.Tenant,run.AgentRunId),v.Request.RequestId,receipt.OperationId,v.Frame.Generation,receipt.ActionHash));
        var state=(await f.Iam.ReadTenantAsync(v.Setup.Tenant))!;var assignment=Assert.Single(state.Assignments,a=>a.PrincipalId==v.Setup.Agent.PrincipalId);
        await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId,v.Setup.Tenant,assignment.AssignmentId,state.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>service.RequireStoredAsync(run).AsTask());Assert.Equal(1,native.Calls);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_http_abandoned_reservation_recovers_after_reopen_without_a_native_retry(bool sqlite)
    {
        var clock=new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T14:00:00Z"));await using var f=await Fixture.Create(sqlite,clock,builtInAuthority:true);var v=await ApprovedHttp(f,true);
        var state=(await f.Iam.ReadTenantAsync(v.Setup.Tenant))!;var run=(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(v.Setup.Tenant,v.Frame.RunId))!;
        var decision=(await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(v.Setup.Tenant,v.Request.RequestId))!;
        var grant=(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(v.Setup.Tenant,v.Request.RequestId))!;
        var commit=new TenantOperationCommit(v.Request.RequestId,v.Request.Operation.OperationId,run.AgentRunId,v.Frame.Generation,run.Revision,v.Request.Revision,decision.DecisionId,grant.Revision,v.Request.Operation.ActionHash,TenantOperationOutcome.Completed,"operation_completed");
        var started=await ((ITenantHttpOperationStore)f.Iam).TryStartHttpEffectAsync(state with { Tenant=state.Tenant with { Revision=state.Tenant.Revision+1 } },state.Tenant.Revision,commit,
            new(f.Ids.NewId(),v.Setup.Tenant,v.Setup.Agent.PrincipalId,TenantHttpEffectRules.Dispatch,v.Request.ResourceId,"allowed","http_request_dispatched",clock.GetUtcNow()));
        Assert.NotNull(started!.Fence);var native=new OwnedHttpPort();Assert.Equal(0,await HttpEffects(f,native).RecoverAsync());clock.Advance(TimeSpan.FromMinutes(6));
        var current=sqlite?new SqliteIamStore(f.Contexts!,builtIns:f.BuiltIns,clock:f.Clock):f.Iam;var service=HttpEffects(f,native,current);
        Assert.Equal(1,await service.RecoverAsync());Assert.Equal(0,await service.RecoverAsync());Assert.Equal(0,native.Calls);
        var receipt=(await ((ITenantApprovedOperationStore)current).FindOperationReceiptAsync(v.Setup.Tenant,v.Request.Operation.OperationId))!;Assert.Equal(TenantOperationOutcome.Indeterminate,receipt.Outcome);
        Assert.Equal(OperationGrantStatus.Indeterminate,(await ((ITenantApprovalStore)current).FindOperationGrantAsync(v.Setup.Tenant,v.Request.RequestId))!.Status);
        Assert.Equal(receipt,await service.ExecuteApprovedAsync(new(v.Setup.Tenant,run.AgentRunId),v.Request.RequestId,receipt.OperationId,v.Frame.Generation,receipt.ActionHash));Assert.Equal(0,native.Calls);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_http_noncooperative_native_timeout_is_indeterminate_and_late_body_never_returns(bool sqlite)
    {
        var clock=new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T14:00:00Z"));await using var f=await Fixture.Create(sqlite,clock,builtInAuthority:true);var v=await ApprovedHttp(f);
        var native=new OwnedHttpPort { Hold=true };var service=HttpEffects(f,native);var pending=service.ExecuteApprovedAsync(new(v.Setup.Tenant,v.Frame.RunId),v.Request.RequestId,v.Request.Operation.OperationId,v.Frame.Generation,v.Request.Operation.ActionHash).AsTask();
        await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));clock.Advance(TimeSpan.FromSeconds(40));var receipt=await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(TenantOperationOutcome.Indeterminate,receipt.Outcome);native.Release.TrySetResult();await native.Returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(receipt,await service.ExecuteApprovedAsync(new(v.Setup.Tenant,v.Frame.RunId),v.Request.RequestId,receipt.OperationId,v.Frame.Generation,receipt.ActionHash));Assert.Equal(1,native.Calls);Assert.Null(receipt.Http);
    }
    private sealed class OwnedHttpPort:IHttpRequestClient
    {
        public bool SupportsTenantDispatchChecks=>true;public int Calls;public bool Hold;
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Returned { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<HttpToolResponse> SendAsync(HttpToolRequest request,CancellationToken cancellationToken=default)
        {
            Calls++;await request.AuthorizeDispatch!(cancellationToken);Entered.TrySetResult();if(Hold)await Release.Task;Returned.TrySetResult();
            return new(200,request.Url.AbsoluteUri,"text/plain","HTTP-OWNED-RESPONSE",false,true,null,null,null);
        }
    }
}
