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
        await using var runtime=s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot,new AgentCore.Application.Testing.CapturingSessionOutput());
        await runtime.AttachAsync();Assert.True(await runtime.SubmitUserTextAsync("Hello"));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEmpty(model.Requests);
        Assert.All(model.Requests,request=>{
            Assert.DoesNotContain(request.Tools??[],tool=>ManagementTools.IsManagement(tool.Name));
            var system=string.Join('\n',request.Messages.Where(m=>m.Role==ModelRole.System).Select(m=>m.Text));
            foreach(var forbidden in new[]{"configuration.inspect","builtin:configuration-assistance","builtin:harness-authoring","Use harness.inspect"})Assert.DoesNotContain(forbidden,system);
        });
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
