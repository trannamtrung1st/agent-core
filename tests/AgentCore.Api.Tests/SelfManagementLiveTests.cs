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
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace AgentCore.Api.Tests;

public sealed class SelfManagementLiveTests(ITestOutputHelper output)
{
    [SelfManagementLiveFact(Timeout = 480000)]
    public async Task Real_model_discovers_configuration_saves_with_risk_floors_reads_next_Run_and_freezes()
    {
        var options = new LanguageModelProviderOptions { Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/",
            ApiKey = LiveIdentityMaintenanceTests.Key(), DefaultModel = Environment.GetEnvironmentVariable("AGENTCORE_SELF_MANAGEMENT_MODEL") ?? "deepseek/deepseek-v4.1-flash",
            Tools = true, StructuredOutput = false };
        using var http = new HttpClient();
        var model = new ObservedModel(new SemanticResponseLanguageModel(new OpenAICompatibleLanguageModel(http, options)));
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"self-management-live-{Guid.NewGuid():N}.db"), model,
            configure: services => {
                services.RemoveAll<IModelCatalog>(); services.AddSingleton<IModelCatalog>(ModelCatalogFactory.Create("Real", options, null));
                services.RemoveAll<ILanguageModelResolver>(); services.AddSingleton<ILanguageModelResolver>(new Resolver(model));
            }, configuration: c => c.AddInMemoryCollection(new Dictionary<string, string?> {
                ["AgentCore:Profile"] = "Real", ["Providers:LanguageModels:primary-llm:Adapter"] = "OpenAICompatible",
                ["Providers:LanguageModels:primary-llm:BaseUrl"] = options.BaseUrl, ["Providers:LanguageModels:primary-llm:ApiKey"] = options.ApiKey,
                ["Providers:LanguageModels:primary-llm:DefaultModel"] = options.DefaultModel,
                ["Providers:LanguageModels:primary-llm:Tools"] = "true", ["Providers:LanguageModels:primary-llm:StructuredOutput"] = "false" }));
        var s = host.Services;
        var owner = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 23);
        var policy = s.GetRequiredService<SelfManagementService>();
        var view = await policy.UpdateAsync(owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Managed), new([HarnessManagementScope.Settings, HarnessManagementScope.Skills]), new([ManagementAutoApply.ConversationPreferences])));
        var snapshot = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(owner.InstanceId, SessionMode.Text);
        var sink = new Output();
        await using var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, sink);
        await runtime.AttachAsync();
        async Task Turn(string text, params string[] approvals)
        {
            var before = model.Calls.Count;
            Assert.True(await runtime.SubmitUserTextAsync(text));
            var idle = runtime.WaitUntilIdleAsync();
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (!idle.IsCompleted && DateTimeOffset.UtcNow < deadline)
            {
                while (sink.Approvals.TryDequeue(out var pending))
                {
                    Assert.Contains(pending.Action.ToolName, approvals);
                    Assert.Contains("Instance revision", pending.Action.Details.Keys);
                    Assert.Equal(ResponseApprovalResult.Accepted, await runtime.RespondApprovalAsync(pending.Response, pending.Action.ApprovalId, ToolApprovalDecision.Approve));
                }
                await Task.WhenAny(idle, Task.Delay(100));
            }
            await idle.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Empty(sink.Errors);
            output.WriteLine("Turn tools: " + string.Join(", ", model.Calls.Skip(before).Select(c => c.Name)));
            output.WriteLine("Reply: " + runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Text);
        }
        await Turn("Inspect your current configuration and tell me your response length.");
        Assert.Contains(model.Calls, c => c.Name == "configuration.inspect");
        await Turn("Always answer briefly. Save that preference for this Instance.");
        Assert.Equal("concise", (await s.GetRequiredService<AgentInstanceSettingsService>().ReadAsync(owner.InstanceId, "conversationPolicy")).Effective["responseLength"]);
        await Turn("Enable user memory retrieval for this Instance. Ask for the exact configuration approval.", "settings.patch", "configuration.propose");
        Assert.Equal(true, (await s.GetRequiredService<AgentInstanceSettingsService>().ReadAsync(owner.InstanceId, "memoryPolicy")).Effective["userRetrieval"]);
        await Turn("Read back my saved response length and user memory retrieval setting.");
        var runs = await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(owner.InstanceId, LocalUserProfile.Id), snapshot.SessionId);
        Assert.Contains(runs, r => r.Admission.Configuration?.Definition.MemoryPolicy?.UserRetrieval == true);
        await Turn("Freeze self-management now.", "management.freeze", "management.policy.update");
        Assert.True((await policy.ReadAsync(owner.InstanceId)).Effective.Frozen);
        var requestsBefore = model.Requests.Count;
        await Turn("What is two plus two?");
        Assert.All(model.Requests.Skip(requestsBefore), r => {
            Assert.DoesNotContain(r.Tools ?? [], t => ManagementTools.IsManagement(t.Name));
            var system = string.Join('\n', r.Messages.Where(m => m.Role == ModelRole.System).Select(m => m.Text));
            Assert.DoesNotContain("configuration.inspect", system);
            Assert.DoesNotContain("builtin:configuration-assistance", system);
        });
    }
    private sealed class Resolver(ILanguageModel model) : ILanguageModelResolver
    { public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose) => model; }
    private sealed class Output : ISessionOutput
    {
        public ConcurrentQueue<(Guid Response, ApprovalRequestedOutput Action)> Approvals { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public ValueTask PublishAsync(SessionOutput item, CancellationToken ct = default)
        {
            if (item.Payload is ApprovalRequestedOutput approval) Approvals.Enqueue((item.ResponseId!.Value, approval));
            if (item.Payload is ErrorOutput error) Errors.Enqueue(error.SafeMessage);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class ObservedModel(ILanguageModel inner) : ILanguageModel
    {
        public ModelCapabilities Capabilities => inner.Capabilities;
        public List<ModelToolCall> Calls { get; } = [];
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() });
            await foreach (var e in inner.GenerateAsync(request, ct)) { if (e is ModelToolCallEvent call) Calls.Add(call.Call); yield return e; }
        }
    }
}
public sealed class SelfManagementLiveFactAttribute : FactAttribute
{
    public SelfManagementLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_LIVE_SELF_MANAGEMENT") != "1") Skip = "Explicit AGENTCORE_LIVE_SELF_MANAGEMENT=1 required.";
        else if (string.IsNullOrWhiteSpace(LiveIdentityMaintenanceTests.Key())) Skip = "OpenRouter key missing.";
    }
}
