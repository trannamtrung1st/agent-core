using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantPublicWebReadService PublicReads(Fixture f, PublicReadPort native) => new(Operations(f), (ITenantRunStore)f.Iam, (ITenantSessionStore)f.Iam,
        f.RunConfiguration(), f.Authorizer, f.Iam, native, native, f.Clock, ToolConfigurationGates.AllowAll);
    private static ModelToolCall PublicCall(string tool) => new("public-observation", tool, tool==ToolCatalog.WebSearch?"{\"query\":\"public source\",\"limit\":2}":"{\"url\":\"https://public.example/page\"}");
    [Theory]
    [InlineData(false,"web.search")] [InlineData(true,"web.search")] [InlineData(false,"web.fetch")] [InlineData(true,"web.fetch")]
    public async Task Tenant_public_web_stored_observations_recheck_current_authority_without_refetching(bool sqlite,string tool)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true); var v=await WorkspaceEffectCase(f); var native=new PublicReadPort(); var service=PublicReads(f,native); var call=PublicCall(tool);
        await WorkspaceEffectCheckpoint(f,v.Frame,call); var text=await service.InspectAsync(v.Frame,call,16000);
        var run=(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(v.Setup.Tenant,v.Frame.RunId))!;
        run=await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner,run.AgentRunId,new AgentRunCommand.Checkpoint(run.Revision,f.Clock.GetUtcNow(),v.Frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant,"",ToolCalls:[call]),new(ModelRole.Tool,text,ToolCallId:call.Id,Name:call.Name)]),1,text.Length,100000,100),null),f.Ids.NewId());
        await service.RequireStoredAsync(run); Assert.Equal(1,native.Calls);
        await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId,v.Setup.Tenant,v.Grant.AssignmentId,(await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>service.RequireStoredAsync(run).AsTask()); Assert.Equal(1,native.Calls);
    }
    [Theory]
    [InlineData(false,"web.search")] [InlineData(true,"web.search")] [InlineData(false,"web.fetch")] [InlineData(true,"web.fetch")]
    public async Task Tenant_public_web_deadline_bounds_a_noncooperative_source_and_discards_its_late_result(bool sqlite,string tool)
    {
        var clock=new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T13:00:00Z")); await using var f=await Fixture.Create(sqlite,clock,builtInAuthority:true);
        var v=await WorkspaceEffectCase(f); var native=new PublicReadPort { Hold=true }; var service=PublicReads(f,native); var call=PublicCall(tool);
        await WorkspaceEffectCheckpoint(f,v.Frame,call); var pending=service.InspectAsync(v.Frame,call,16000).AsTask(); await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromSeconds(120)); var result=await pending.WaitAsync(TimeSpan.FromSeconds(15));
        using var json=JsonDocument.Parse(result); Assert.Equal("timeout",json.RootElement.GetProperty("error").GetString()); Assert.DoesNotContain("PUBLIC-NATIVE-WITNESS",result);
        native.Release.TrySetResult(); await native.Returned.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(1,native.Calls);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_public_web_rejects_mismatched_frames_before_native_IO_and_preserves_a_valid_bounded_observation(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true); var v=await WorkspaceEffectCase(f); var native=new PublicReadPort(); var service=PublicReads(f,native); var call=PublicCall(ToolCatalog.WebFetch);
        await WorkspaceEffectCheckpoint(f,v.Frame,call);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>service.InspectAsync(v.Frame with { Generation=Guid.NewGuid() },call,16000).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(()=>service.InspectAsync(v.Frame with { SessionId=Guid.NewGuid() },call,16000).AsTask()); await Assert.ThrowsAsync<AccessDeniedException>(()=>service.InspectAsync(v.Frame with { Owner=AgentRunOwner.ForTenant(Guid.NewGuid(),v.Frame.Owner.AgentInstanceId,v.Frame.Owner.ExecutorPrincipalId!.Value) },call,16000).AsTask()); Assert.Equal(0,native.Calls);
        using var small=JsonDocument.Parse(await service.InspectAsync(v.Frame,call,500)); Assert.True(small.RootElement.GetProperty("tenantPublicWebRead").GetBoolean()); Assert.True(small.RootElement.GetProperty("truncated").GetBoolean());
    }
    [Theory]
    [InlineData(false,"unowned")] [InlineData(true,"unowned")]
    [InlineData(false,"hash")] [InlineData(true,"hash")]
    [InlineData(false,"error-body")] [InlineData(true,"error-body")]
    [InlineData(false,"error-message")] [InlineData(true,"error-message")]
    [InlineData(false,"error-recovery")] [InlineData(true,"error-recovery")]
    public async Task Tenant_public_web_retained_unowned_or_changed_observations_cannot_reenter_the_model(bool sqlite,string mode)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true); var v=await WorkspaceEffectCase(f); var native=new PublicReadPort(); var service=PublicReads(f,native); var call=PublicCall(ToolCatalog.WebFetch);
        var text=mode=="error-message" ? "{\"error\":\"invalid\",\"message\":\"UNOWNED-ERROR-MESSAGE\"}" : mode=="error-recovery" ? "{\"error\":\"invalid\",\"invalidCallRecovery\":{\"instruction\":\"UNOWNED-RECOVERY-BODY\"}}" : mode=="unowned" ? "{\"text\":\"UNOWNED-OBSERVATION\"}" : mode=="hash" ? JsonSerializer.Serialize(new { tenantPublicWebRead=true,readCommandHash=new string('0',64),text="CHANGED-OBSERVATION",untrusted=true }) : "{\"error\":\"invalid\",\"text\":\"UNOWNED-ERROR-BODY\"}";
        var run=(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(v.Setup.Tenant,v.Frame.RunId))!;
        run=await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner,run.AgentRunId,new AgentRunCommand.Checkpoint(run.Revision,f.Clock.GetUtcNow(),v.Frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant,"",ToolCalls:[call]),new(ModelRole.Tool,text,ToolCallId:call.Id,Name:call.Name)]),1,text.Length,100000,100),null),f.Ids.NewId());
        var error=await Assert.ThrowsAsync<AccessDeniedException>(()=>service.RequireStoredAsync(run).AsTask()); Assert.Equal("public_web_source_changed",error.ReasonCode); Assert.Equal(0,native.Calls);
    }
    private sealed class PublicReadPort : IWebSearchProvider, IPublicWebFetcher
    {
        public bool IsAvailable=>true; public bool Hold { get; init; } public int Calls;
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Returned { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Begin() { Interlocked.Increment(ref Calls); Entered.TrySetResult(); if(Hold)await Release.Task; Returned.TrySetResult(); }
        public async ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request,CancellationToken cancellationToken=default) { await Begin(); return new([new("Public","https://public.example/page","PUBLIC-NATIVE-WITNESS")],false); }
        public async ValueTask<PublicWebFetchResult> FetchAsync(PublicWebFetchRequest request,CancellationToken cancellationToken=default) { await Begin(); return new("https://public.example/final","text/plain",new string('試',10000)+"PUBLIC-NATIVE-WITNESS",false,null,null); }
    }
}
