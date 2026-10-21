using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private sealed class ContinuityModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public Guid Source; public bool AskFollowup;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (Source != Guid.Empty)
            {
                if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ContinuityGet))
                {
                    Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.ContinuityGet);
                    yield return new ModelToolCallEvent(new("historical-page", ToolCatalog.ContinuityGet, JsonSerializer.Serialize(new { kind = "Session", id = Source })));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
                Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
                if (AskFollowup)
                {
                    yield return new ModelToolCallEvent(new("after-history", ToolCatalog.AgentStateInspect, "{}")); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
            }
            yield return new ModelSemanticResponseReady(new(Source == Guid.Empty ? "Source seeded." : "Captured historical answer.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    [Theory(Timeout = 120000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_continuity_actual_HTTP_model_source_change_blocks_captured_output_or_a_further_provider_call(bool sqlite, bool nextProvider)
    {
        var model = new ContinuityModel(); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, instance, operations: true); var source = await MemoryRequest(factory, owner, tenant, instance);
        model.Source = source.Session; model.AskFollowup = nextProvider; var before = model.Requests.Count;
        var submitted = await SubmitProfileRequest(owner, tenant, instance); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var active = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
            Assert.Single(TenantHistoricalReferences.Read(active.Checkpoint));
            var route = $"/api/v2/tenants/{tenant}/sessions/{source.Session}"; var metadata = await Json(await owner.GetAsync(route));
            Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(route, new { title = "Changed owned source", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = metadata.GetProperty("revision").GetInt64() })).StatusCode);
            model.Release.TrySetResult(); var terminal = await ProfileTerminal(factory, tenant, submitted.Run); Assert.NotEqual(AgentRunStatus.Completed, terminal.Status);
            Assert.Equal(2, model.Requests.Count - before); var history = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{submitted.Session}/messages")); Assert.DoesNotContain("Captured historical answer.", history.GetRawText());
        }
        finally { model.Release.TrySetResult(); }
    }
}
