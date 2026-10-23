using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    private sealed class StagedMemoryModel(string mode) : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var first = new ModelSemanticResponseReady(new("First.", new(ModelSpeechMode.None, null), [], [Learn("ORIGINAL_STAGED_FACT")]));
            yield return first;
            if (mode == "failure") { yield return new ModelFailed(new(ProviderErrorCode.Authentication, "Controlled Synthetic failure after staging.")); yield break; }
            if (mode == "cancel")
            {
                Parked.TrySetResult();
                try { await Release.Task.WaitAsync(ct); } catch (OperationCanceledException) { yield break; }
            }
            if (mode == "replace") yield return new ModelSemanticResponseReady(new("Replacement.", new(ModelSpeechMode.None, null), [], [Learn("REPLACEMENT_STAGED_FACT")]));
            if (mode == "repeat") yield return first;
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    [Theory(Timeout = 120000)]
    [InlineData(false, "failure")] [InlineData(true, "failure")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "replace")] [InlineData(true, "replace")] [InlineData(false, "repeat")] [InlineData(true, "repeat")]
    public async Task Tenant_learned_memory_staging_uses_only_the_completed_current_response_and_never_a_global_service(bool sqlite, string mode)
    {
        var model = new StagedMemoryModel(mode); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, instance);
        var submitted = await SubmitProfileRequest(owner, tenant, instance);
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        if (mode == "cancel")
        {
            await model.Parked.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var running = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-runs/{submitted.Run}/cancel", new {
                expectedRevision = await Revision(owner, tenant), expectedRunRevision = running.Revision })).StatusCode);
            model.Release.TrySetResult();
        }
        var run = await ProfileTerminal(factory, tenant, submitted.Run); var committed = mode is "replace" or "repeat";
        if (committed) Assert.Equal(AgentRunStatus.Completed, run.Status);
        else Assert.Equal(mode == "cancel" ? AgentRunStatus.Cancelled : AgentRunStatus.Failed, run.Status);
        foreach (var scope in new[] { "identityUser", "user", "session&sessionId=" + submitted.Session })
        {
            var page = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{instance}/memory?scope={scope}"));
            Assert.Equal(committed ? 1 : 0, page.GetProperty("retainedCount").GetInt32());
            if (committed) Assert.Equal(mode == "replace" ? "REPLACEMENT_STAGED_FACT" : "ORIGINAL_STAGED_FACT",
                Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("content").GetString());
        }
        var history = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{submitted.Session}/messages"));
        var assistants = history.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("role").GetString() == "assistant").ToArray();
        if (committed)
        {
            var assistant = Assert.Single(assistants); Assert.Equal(mode == "replace" ? "Replacement." : "First.", assistant.GetProperty("text").GetString());
            Assert.Equal("stored", Assert.Single(assistant.GetProperty("memoryReceipts").EnumerateArray()).GetProperty("outcome").GetString());
        }
        else Assert.All(assistants, item => Assert.False(item.TryGetProperty("memoryReceipts", out var receipts) && receipts.ValueKind == System.Text.Json.JsonValueKind.Array && receipts.GetArrayLength() > 0));
    }
}
