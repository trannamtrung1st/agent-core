using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Long_Run_compacts_inside_its_claim_and_retains_every_exact_read_receipt(bool sqlite)
    {
        var model = new LongResearchModel(); var fetch = new LongResearchFetch();
        void Configure(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<IPublicWebFetcher>(fetch));
        await using var factory = new RuntimeStateFactory(sqlite, model: model, configure: Configure, nativeDefinitions: true);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var instance = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/chat/agents"))).GetProperty("items")[0].GetProperty("instanceId").GetGuid();
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Read all twelve public source pages; keep the original task and verify the last page.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var run = await CompactionTerminal(factory, tenant, submitted.Run);
        Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure}");
        Assert.Equal(submitted.Run, run.AgentRunId); Assert.Equal(1, run.AttemptCount); Assert.Equal(12, fetch.Calls);
        Assert.True(model.SummaryCalls > 0); Assert.True(model.SawSummary);
        var state = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint);
        Assert.NotNull(state.Compaction); Assert.Equal(model.SummaryCalls, state.Compaction.Count);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var receipts));
        Assert.Equal(12, receipts!.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WebFetch));
        for (var i = 1; i <= 12; i++) Assert.Contains(receipts!, m => m.ToolCallId == "long-read-" + i && m.Text.Contains("EVIDENCE-" + i));
        Assert.Equal(12, run.Checkpoint!.StepCount);
        Assert.True(run.Checkpoint.OutputBytes > 12 * 1000); Assert.Equal(run.ResponseId, model.Response);
        Assert.Equal(run.Checkpoint.StepCount, AgentRunToolCallCheckpoint.NormalizeResumedStepCount(run.Checkpoint.StepCount, receipts!));
        var details = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agent-runs/{run.AgentRunId}"));
        Assert.DoesNotContain("ArchivedMessages", details.ToString()); Assert.DoesNotContain("untrusted summary payload", details.ToString());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Invalid_or_failed_summary_keeps_exact_checkpoint_and_continues_only_when_capacity_fits(bool providerFailure)
    {
        var model = new LongResearchModel { RejectSummary = true, SummaryProviderFailure = providerFailure }; var fetch = new LongResearchFetch();
        void Configure(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<IPublicWebFetcher>(fetch));
        await using var factory = new RuntimeStateFactory(false, model: model, configure: Configure, nativeDefinitions: true, manualDispatch: true);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var instance = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/chat/agents"))).GetProperty("items")[0].GetProperty("instanceId").GetGuid();
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Read twelve public pages and preserve all observed results.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var done = await CompactionTerminal(factory, tenant, submitted.Run);
        Assert.Equal(AgentRunStatus.Completed, done.Status); Assert.Equal(12, fetch.Calls);
        var state = AgentRunToolCallCheckpoint.ReadExecutionState(done.Checkpoint);
        Assert.Null(state.Compaction); Assert.InRange(state.CompactionAttempts, 1, RunContextCompaction.MaxCompactions);
        Assert.Equal(state.CompactionAttempts, model.SummaryCalls);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(done.Checkpoint, out var receipts));
        Assert.Equal(12, receipts!.Count(m => m.Role == ModelRole.Tool));
    }

    [Fact]
    public async Task Compacted_SQLite_Run_resumes_after_host_recreation_without_repeating_reads()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var root = Path.Combine(Path.GetTempPath(), "compacted-run-reopen-" + Guid.NewGuid().ToString("N"));
        Guid tenant; Guid runId; var fetch = new LongResearchFetch(); var firstModel = new LongResearchModel { HoldAfterCompaction = true };
        void Configure(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<IPublicWebFetcher>(fetch));
        await using (var first = new RuntimeStateFactory(true, clock, firstModel, root: root, configure: Configure, nativeDefinitions: true, manualDispatch: true))
        {
            using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var instance = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/chat/agents"))).GetProperty("items")[0].GetProperty("instanceId").GetGuid();
            var editor = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{instance}/editor"));
            var changed = await Json(await owner.PutAsJsonAsync($"/api/v2/tenants/{tenant}/agents/{instance}/execution-budgets", new {
                expectedRevision = await Revision(owner, tenant),
                expectedInstanceRevision = editor.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64(),
                budgets = new AgentCore.Domain.Definitions.ExecutionBudgetPolicy(Standard: new(24, 600), InteractiveBrowser: new(48, 600), UnattendedBoundBrowser: new(32, 600)) }));
            runId = (await SubmitProfileRequest(owner, tenant, instance, instanceRevision: changed.GetProperty("instance").GetProperty("revision").GetInt64(), message: "Read twelve public pages and keep each exact source receipt.")).Run;
            await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            await firstModel.Held.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var retained = (await first.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
            Assert.NotNull(AgentRunToolCallCheckpoint.ReadExecutionState(retained.Checkpoint).Compaction);
            Assert.False(retained.IsTerminal);
        }
        var beforeReads = fetch.Calls;
        clock.Advance(AgentRunCoordinator.ClaimDuration + TimeSpan.FromSeconds(2));
        var resumed = new LongResearchModel { StartFromRetainedRound = true };
        await using var second = new RuntimeStateFactory(true, clock, resumed, root: root, configure: Configure, nativeDefinitions: true, manualDispatch: true);
        using var triggerStartup = second.CreateClient();
        await second.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var done = await CompactionTerminal(second, tenant, runId);
        Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}; reads={fetch.Calls}; summaries={resumed.SummaryCalls}; steps={done.Checkpoint?.StepCount}");
        Assert.Equal(12, fetch.Calls); Assert.InRange(beforeReads, 3, 11); Assert.True(resumed.SawSummary);
        Assert.Equal(runId, done.AgentRunId); Assert.Equal(2, done.AttemptCount);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(done.Checkpoint, out var receipts));
        Assert.Equal(12, receipts!.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WebFetch));
    }

    private static async Task<AgentRun> CompactionTerminal(WebApplicationFactory<Program> factory, Guid tenant, Guid id)
    {
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        AgentRun? latest = null;
        try
        {
            while (true)
            {
                latest = await runs.FindTenantRunAsync(tenant, id, timeout.Token);
                if (latest!.IsTerminal) return latest;
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException($"Compaction Run did not settle: status={latest?.Status}; steps={latest?.Checkpoint?.StepCount}; attempt={latest?.AttemptCount}; failure={latest?.Failure?.Code}");
        }
    }

    private sealed class LongResearchFetch : IPublicWebFetcher
    {
        public bool IsAvailable => true; public int Calls;
        public ValueTask<PublicWebFetchResult> FetchAsync(PublicWebFetchRequest request, CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(new PublicWebFetchResult(request.Url.AbsoluteUri, "text/plain", "EVIDENCE-" + count + " " + new string('x', 18000), false, null, null));
        }
    }
    private sealed class LongResearchModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        private int round; public bool RejectSummary; public bool SummaryProviderFailure; public bool HoldAfterCompaction; public bool StartFromRetainedRound;
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SummaryCalls; public bool SawSummary; public Guid? Response;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Response ??= request.ResponseId; Assert.Equal(Response, request.ResponseId);
            if (request.Context?.Text(ContextSectionId.History).StartsWith(RunContextCompaction.Marker, StringComparison.Ordinal) == true)
            {
                SummaryCalls++; Assert.Null(request.Tools);
                if (RejectSummary)
                {
                    if (SummaryProviderFailure) yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Synthetic summary provider failure."));
                    else { yield return new ModelTextDelta("{broken"); yield return new ModelCompleted(ModelStopReason.Completed); }
                    yield break;
                }
                yield return new ModelTextDelta("""{"objective":"Read all twelve public pages","completed":"Earlier read evidence retained","decisions":"Use public sources","pending":"Remaining pages","constraints":"Original task","next":"Continue reading","uncertainty":"untrusted summary payload"}""");
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            Assert.Contains(request.Context!.Sections, s => s.Id == ContextSectionId.AgentInstructions && s.Layer == ContextLayer.Runtime);
            if (request.Context.Sections.Any(s => s.Id == ContextSectionId.RunSummary))
            {
                SawSummary = true;
                if (HoldAfterCompaction) { Held.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                if (StartFromRetainedRound && round == 0)
                {
                    var latest = request.Messages.Last(m => m.Role == ModelRole.Tool && m.ToolCallId!.StartsWith("long-read-", StringComparison.Ordinal));
                    round = int.Parse(latest.ToolCallId!["long-read-".Length..]);
                }
                Assert.Equal(ContextSemantics.UntrustedData, request.Context.Sections.Single(s => s.Id == ContextSectionId.RunSummary).Semantics);
                Assert.True(request.Messages.Count(m => m.Role == ModelRole.Tool) < round);
                Assert.Contains(request.Messages, m => m.Role == ModelRole.Tool && m.ToolCallId == "long-read-" + round);
            }
            if (round < 12)
            {
                round++;
                yield return new ModelToolCallEvent(new("long-read-" + round, ToolCatalog.WebFetch, "{\"url\":\"https://public.example/page/" + round + "\"}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                yield return new ModelTextDelta("All twelve public pages were read; the final page was observed.");
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }
}
