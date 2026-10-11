using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed class SelfManagementContinuationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Captured_unrelated_requests_omit_management_for_off_frozen_and_enabled_instances(bool enabled, bool frozen)
    {
        var model=new ObservedModel(new ScriptedLanguageModel());
        await using var host=new ExperienceHost(Path.Combine(Path.GetTempPath(),$"configuration-context-{Guid.NewGuid():N}.db"),model);
        var s=host.Services;
        var owner=await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",24);
        if(enabled) {
            var policy=await s.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId,owner.Revision,
                new(new(HarnessManagementMode.Managed),new(Enum.GetValues<HarnessManagementScope>())));
            if(frozen)await s.GetRequiredService<SelfManagementService>().FreezeAsync(owner.InstanceId,policy.InstanceRevision);
        }
        var snapshot=await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(owner.InstanceId,SessionMode.Text);
        await using var runtime=s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot,new Output());
        await runtime.AttachAsync();Assert.True(await runtime.SubmitUserTextAsync("Hello"));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEmpty(model.Requests);
        Assert.All(model.Requests,request=>{
            Assert.DoesNotContain(request.Tools??[],tool=>ManagementTools.IsManagement(tool.Name));
            var system=string.Join('\n',request.Messages.Where(m=>m.Role==ModelRole.System).Select(m=>m.Text));
            foreach(var forbidden in new[]{"configuration.inspect","builtin:configuration-assistance","builtin:harness-authoring","Use harness.inspect"})Assert.DoesNotContain(forbidden,system);
        });
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, true)]
    public async Task Exact_approval_continues_once_with_fresh_pins_and_rejection_never_continues(bool approve, bool restart, bool cancel, bool freeze)
    {
        var continued=approve && !freeze;
        var db=Path.Combine(Path.GetTempPath(),$"configuration-continuation-{Guid.NewGuid():N}.db");
        var model=new ObservedModel(new ScriptedLanguageModel());
        var logs=new DiagnosticLineLoggerProvider();
        await using var host=new ExperienceHost(db,model,configure:services=>services.AddLogging(b=>b.AddProvider(logs)));
        var s=host.Services;
        var owner=await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",24);
        var policy=await s.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId,owner.Revision,
            new(new(HarnessManagementMode.Assisted),new([HarnessManagementScope.Settings])));
        var definition=(await s.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        await s.GetRequiredService<AgentInstanceSettingsService>().PatchAsync(owner.InstanceId,"capabilities",policy.InstanceRevision,
            new Dictionary<string,System.Text.Json.JsonElement> { ["selected"]=System.Text.Json.JsonSerializer.SerializeToElement(
                RoleEnvironments.Of(definition).ToolList.Where(t=>t!=ToolCatalog.WorkspaceList).ToArray()) },[]);
        ExperienceHost? restartHost=null;
        var before=(await s.GetRequiredService<SelfManagementService>().ReadAsync(owner.InstanceId)).InstanceRevision;
        var snapshot=await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(owner.InstanceId,SessionMode.Text);
        var sink=new Output();var disposed=false;var runtime=s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot,sink);
        try {
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("List my workspace after enabling workspace listing."));
            var pending=await sink.Approval.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("configuration.propose",pending.Action.ToolName);
            if(freeze)await s.GetRequiredService<SelfManagementService>().FreezeAsync(owner.InstanceId,before);
            if(restart) {
                await runtime.DisposeAsync();disposed=true;await host.DisposeAsync();
                restartHost=new ExperienceHost(db,model);s=restartHost.Services;
                var restored=(await s.GetRequiredService<IMemoryStore>().LoadAsync(snapshot.SessionId))!;
                sink=new Output();runtime=s.GetRequiredService<SessionRuntimeFactory>().Create(restored,sink);disposed=false;
                await runtime.AttachAsync();
                var waiting=Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(owner.InstanceId,LocalUserProfile.Id),snapshot.SessionId));
                Assert.Equal(AgentRunStatus.WaitingForApproval,waiting.Status);
                var a=waiting.Approval!;
                var queued=await runtime.ControlAgentRunAsync(waiting.AgentRunId,waiting.Revision,a.ApprovalId,a.Revision,a.ActionHash,AgentRunApprovalDecision.Approved);
                var now=DateTimeOffset.UtcNow;
                var claimed=await s.GetRequiredService<IAgentRunStore>().ApplyAsync(queued.Owner,queued.AgentRunId,new AgentRunCommand.Claim(queued.Revision,now,Guid.NewGuid(),now.AddMinutes(5)));
                Assert.True(await runtime.DispatchAgentRunAsync(claimed.AgentRunId,false));
            }
            else if(cancel)await runtime.CancelResponseAsync(pending.Response);
            else Assert.Equal(ResponseApprovalResult.Accepted,await runtime.RespondApprovalAsync(pending.Response,pending.Action.ApprovalId,
                approve?ToolApprovalDecision.Approve:ToolApprovalDecision.Reject));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(sink.Errors.IsEmpty,string.Join("\n",logs.Lines));
            var runs=await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(owner.InstanceId,LocalUserProfile.Id),snapshot.SessionId);
            Assert.True(runs.Count==(continued?2:1),string.Join("\n",runs.Select(r=>$"{r.Status}: {r.Result?.Text}; side effect: {r.SideEffect.Disposition}; checkpoint: {r.Checkpoint?.PayloadJson}")));
            var source=Assert.Single(runs,r=>r.Admission.Activation.Kind==ActivationKind.UserTurn);
            Assert.Equal(cancel?AgentRunStatus.Cancelled:AgentRunStatus.Completed,source.Status);
            Assert.Equal(before,source.Admission.Configuration!.InstanceRevision);
            if(continued) {
                var continuation=Assert.Single(runs,r=>r.Admission.Activation.Kind==ActivationKind.ConfigurationContinuation);
                Assert.Equal(AgentRunStatus.Completed,continuation.Status);
                Assert.Equal(source.AgentRunId,continuation.Admission.Activation.SourceAgentRunId);
                Assert.Empty(continuation.Admission.Activation.SourceEntryIds);
                Assert.NotEqual(source.ResponseId,continuation.ResponseId);
                Assert.Equal(before+1,continuation.Admission.Configuration!.InstanceRevision);
                Assert.False(AgentCore.Application.Agents.RolePermissions.AllowsTool(source.Admission.Configuration.Definition,ToolCatalog.WorkspaceList));
                Assert.True(AgentCore.Application.Agents.RolePermissions.AllowsTool(continuation.Admission.Configuration.Definition,ToolCatalog.WorkspaceList));
                Assert.Contains(runtime.Snapshot.Entries,e=>e.Role==ConversationRole.Assistant && e.Text=="Workspace inspected in a fresh Run.");
                Assert.All(model.Requests.Where(r=>r.ResponseId==continuation.ResponseId),r=>Assert.DoesNotContain(r.Tools??[],t=>
                    ManagementTools.IsManagement(t.Name)||ToolRegistry.TryGet(t.Name,out var tool) && tool.Effect!=ToolEffect.ReadOnly));
            }
            Assert.Single(runtime.Snapshot.Entries,e=>e.Role==ConversationRole.User);
            var saved=runtime.Snapshot;
            await runtime.DisposeAsync();disposed=true;
            await using var reopened=s.GetRequiredService<SessionRuntimeFactory>().Create(saved,new Output());
            await reopened.AttachAsync();await reopened.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(continued?2:1,(await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(owner.InstanceId,LocalUserProfile.Id),snapshot.SessionId)).Count);
        }
        finally { if(!disposed)await runtime.DisposeAsync(); if(restartHost is not null)await restartHost.DisposeAsync(); }
    }
    private sealed class Output:ISessionOutput {
        public TaskCompletionSource<(Guid Response,ApprovalRequestedOutput Action)> Approval {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Errors {get;}=new();
        public ValueTask PublishAsync(SessionOutput item,CancellationToken ct=default) {
            if(item.Payload is ApprovalRequestedOutput approval)Approval.TrySetResult((item.ResponseId!.Value,approval));
            if(item.Payload is ErrorOutput error)Errors.Enqueue(error.SafeMessage);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class ObservedModel(ILanguageModel inner):ILanguageModel {
        public ModelCapabilities Capabilities=>inner.Capabilities;
        public ConcurrentQueue<ModelRequest> Requests {get;}=new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,[EnumeratorCancellation]CancellationToken ct=default) {
            Requests.Enqueue(request with {Messages=request.Messages.ToArray()});
            await foreach(var item in inner.GenerateAsync(request,ct))yield return item;
        }
    }
}
