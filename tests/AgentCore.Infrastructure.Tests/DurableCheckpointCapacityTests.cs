using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class DurableCheckpointCapacityTests
{
    [Theory]
    [InlineData(1, 80_000, true)]
    [InlineData(40, 8_000, false)]
    public async Task Large_and_cumulative_reads_fit_or_terminate_with_a_stable_capacity_failure(int reads, int size, bool completes)
    {
        await using var fixture = await Fixture.CreateAsync();
        var fetch = new Fetcher(new string('x', size));
        var model = new ReadModel(reads);
        var outcome = await fixture.RunAsync(new SessionToolExecutor(publicWebFetcher: fetch), model);
        if (completes)
        {
            var completed = Assert.IsType<DurableOccurrenceCompleted>(outcome);
            Assert.Equal(reads, fetch.Calls);
            Assert.Equal("Read the document.", WorkCompletionRequest.Summary(completed.Text));
        }
        else
        {
            var failed = Assert.IsType<DurableOccurrenceFailed>(outcome);
            Assert.Equal("checkpoint-capacity", failed.Code);
            Assert.InRange(fetch.Calls, 2, reads - 1);
            var saved = await fixture.Store.FailAsync(failed.Running.WorkItemId, failed.Running.Revision,
                fixture.Generation, failed.Code, failed.Summary, false, fixture.Now, null);
            Assert.Equal(WorkItemStatus.Failed, saved.Status);
            Assert.Equal(1, saved.AttemptCount);
        }
        Assert.All(model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool), m =>
        {
            using var json = JsonDocument.Parse(m.Text);
            Assert.True(json.RootElement.TryGetProperty("truncated", out _)
                || json.RootElement.TryGetProperty("error", out var error) && error.GetString() == "finish_required");
        });
        Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool
            && m.Text.Contains("\"truncated\":true", StringComparison.Ordinal));
        Assert.All(fixture.Checkpoints, c => Assert.InRange(Encoding.UTF8.GetByteCount(c.PayloadJson), 1, WorkLimits.MaxCheckpointBytes));
    }

    [Theory]
    [InlineData(TriggerKind.ScheduledOccurrence)]
    [InlineData(TriggerKind.ManualInvocation)]
    public async Task Escaped_multibyte_result_survives_sqlite_restart_without_repeating_the_read(TriggerKind triggerKind)
    {
        await using var fixture = await Fixture.CreateAsync(triggerKind == TriggerKind.ManualInvocation
            ? WorkSourceKind.ManualInvocation : WorkSourceKind.Schedule);
        var fetch = new Fetcher(string.Concat(Enumerable.Repeat("資料\n\"\\\u0001", 20_000)));
        var tools = new SessionToolExecutor(publicWebFetcher: fetch);
        await Assert.ThrowsAsync<SimulatedCrash>(() => fixture.RunAsync(tools, new ReadModel(1, thought: triggerKind == TriggerKind.ManualInvocation), crashAfterResult: true, triggerKind: triggerKind));
        Assert.Equal(1, fetch.Calls);
        await fixture.RecoverAsync();
        var outcome = await fixture.RunAsync(tools, new ReadModel(1, thought: triggerKind == TriggerKind.ManualInvocation), triggerKind: triggerKind);
        var completed = Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.False(completed.AttentionRequired);
        Assert.Equal(1, fetch.Calls);
        Assert.All(fixture.Checkpoints, c => Assert.True(DurableToolCallCheckpoint.TryRead(c, out _)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_initial_context_or_tool_batch_fails_once_before_dispatch(bool initial)
    {
        await using var fixture = await Fixture.CreateAsync();
        var fetch = new Fetcher("text");
        var model = new ReadModel(1, oversizedArguments: !initial);
        var request = new ModelRequest(Guid.NewGuid(), [new(ModelRole.User, initial ? new string('x', 70_000) : "read")]);
        var outcome = await fixture.RunAsync(new SessionToolExecutor(publicWebFetcher: fetch), model, request: request);
        var failed = Assert.IsType<DurableOccurrenceFailed>(outcome);
        Assert.Equal("checkpoint-capacity", failed.Code);
        Assert.Equal(0, fetch.Calls);
        Assert.Equal(initial ? 0 : 1, model.Requests.Count);
        var saved = await fixture.Store.FailAsync(failed.Running.WorkItemId, failed.Running.Revision,
            fixture.Generation, failed.Code, failed.Summary, false, fixture.Now, null);
        Assert.Equal(WorkItemStatus.Failed, saved.Status);
        Assert.Equal(1, saved.AttemptCount);
        Assert.Null(await fixture.Store.TryClaimAsync(saved.WorkItemId, Guid.NewGuid(), fixture.Now, fixture.Now.AddMinutes(1)));
    }

    [Fact]
    public async Task Large_write_result_and_crash_preserve_succeeded_fence_and_do_not_repeat_the_write()
    {
        await using var fixture = await Fixture.CreateAsync();
        var http = new Writer();
        var tools = new SessionToolExecutor(httpRequestClient: http, configurationGate: ToolConfigurationGates.AllowAll);
        var request = new ModelRequest(Guid.NewGuid(), [new(ModelRole.User, new string('x', 10_000))]);
        var waiting = Assert.IsType<DurableOccurrenceSuspended>(await fixture.RunAsync(tools, new ReadModel(1, write: true), request: request));
        var approval = waiting.Running.Approval!;
        await fixture.Store.DecideApprovalAsync(waiting.Running.Owner, waiting.Running.WorkItemId, approval.ApprovalId,
            waiting.Running.Revision, approval.Revision, approval.ActionHash, WorkApprovalDecision.Approved, fixture.Now);
        await fixture.ClaimAsync();
        await Assert.ThrowsAsync<SimulatedCrash>(() => fixture.RunAsync(tools, new ReadModel(1, write: true), crashBeforeResult: true));
        Assert.Equal(1, http.Calls);
        var persisted = await fixture.Store.GetAsync(fixture.Running.Owner, fixture.Running.WorkItemId);
        Assert.Equal(WorkSideEffectDisposition.Succeeded, persisted!.SideEffect.Disposition);
        Assert.Empty(DurableToolCallCheckpoint.TryRead(persisted.Checkpoint, out var messages)
            ? messages!.Where(m => m.Role == ModelRole.Tool) : throw new InvalidOperationException());
        await fixture.RecoverAsync();
        var model = new ReadModel(1, write: true);
        var completed = Assert.IsType<DurableOccurrenceCompleted>(await fixture.RunAsync(tools, model));
        Assert.Equal(1, http.Calls);
        Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool
            && m.Text.Contains("already_completed", StringComparison.Ordinal));
        Assert.Equal(WorkSideEffectDisposition.None, completed.Running.SideEffect.Disposition);
    }

    [Theory]
    [InlineData(TriggerKind.ManualInvocation, "NoAction", false)]
    [InlineData(TriggerKind.ManualInvocation, "ActionCompleted", false)]
    [InlineData(TriggerKind.ManualInvocation, "NoAction", true)]
    [InlineData(TriggerKind.ScheduledOccurrence, null, false)]
    public async Task Capacity_notice_retains_maximum_completion_across_SQLite_restart(TriggerKind kind, string? outcome, bool write)
    {
        await using var fixture = await Fixture.CreateAsync(kind == TriggerKind.ManualInvocation ? WorkSourceKind.ManualInvocation : WorkSourceKind.Schedule);
        var fetch = new Fetcher("read must not execute"); var writer = new Writer();
        var tools = new SessionToolExecutor(publicWebFetcher: fetch, httpRequestClient: writer, configurationGate: ToolConfigurationGates.AllowAll);
        var messages = NearCapacity(kind);
        var summary = new string('\u0800', 2000);
        var model = new CapacityModel(summary, outcome, write);
        await Assert.ThrowsAsync<SimulatedCrash>(() => fixture.RunAsync(tools, model, crashAfterCapacityResult: true,
            request: new(Guid.NewGuid(), messages), triggerKind: kind));
        Assert.Equal(0, fetch.Calls); Assert.Equal(0, writer.Calls);
        Assert.Equal(WorkSideEffectDisposition.None, fixture.Running.SideEffect.Disposition);
        Assert.Null(fixture.Running.Approval);
        Assert.Contains(fixture.Checkpoints, c => c.PayloadJson.Contains("finish_required"));
        await fixture.RecoverAsync();
        // Crash again after persisting the maximum completion, before its execution.
        await Assert.ThrowsAsync<SimulatedCrash>(() => fixture.RunAsync(tools, new CapacityModel(summary, outcome, write),
            crashAfterCompletion: true, triggerKind: kind));
        await fixture.RecoverAsync();
        var finalModel = new CapacityModel(summary, outcome, write);
        var completed = Assert.IsType<DurableOccurrenceCompleted>(await fixture.RunAsync(tools, finalModel, triggerKind: kind));
        Assert.Empty(finalModel.Requests); // Pending completion is resumed directly.
        Assert.Equal(summary, WorkCompletionRequest.Summary(completed.Text));
        Assert.Equal(0, fetch.Calls); Assert.Equal(0, writer.Calls);
        Assert.All(fixture.Checkpoints, c => Assert.InRange(Encoding.UTF8.GetByteCount(c.PayloadJson), 1, WorkLimits.MaxCheckpointBytes));
    }

    [Fact]
    public async Task Ignoring_finish_required_fails_stably_without_dispatch()
    {
        await using var fixture = await Fixture.CreateAsync(WorkSourceKind.ManualInvocation);
        var fetch = new Fetcher("must not execute");
        var failed = Assert.IsType<DurableOccurrenceFailed>(await fixture.RunAsync(new(publicWebFetcher: fetch),
            new ReadModel(99, thought: true), request: new(Guid.NewGuid(), NearCapacity(TriggerKind.ManualInvocation)), triggerKind: TriggerKind.ManualInvocation));
        Assert.Equal("checkpoint-capacity", failed.Code); Assert.Equal(0, fetch.Calls);
        Assert.Contains(fixture.Checkpoints, c => c.PayloadJson.Contains("finish_required"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Terminal_checkpoint_round_trips_legacy_and_compact_completion(bool compact)
    {
        var summary = "Escaped \"quote\" and Unicode \u0800\nnext line";
        var args = JsonSerializer.Serialize(new { summary, attentionRequired = false, outcome = "NoAction" });
        var call = new ModelToolCall("legacy-call", ToolCatalog.WorkComplete, args);
        var payload = compact ? DurableToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])])
            : JsonSerializer.Serialize(new { Phase = "model-turn", Messages = new[] {
                new { Role = "Assistant", Text = "", ToolCallId = (string?)null, Name = (string?)null,
                    ToolCalls = new[] { new { call.Id, call.Name, call.ArgumentsJson } } } } });
        Assert.True(DurableToolCallCheckpoint.TryRead(new(payload, 1, 0, 1000), out var messages));
        var resumed = Assert.Single(Assert.Single(messages!).ToolCalls!);
        Assert.Equal(call.Id, resumed.Id);
        using var parsed = JsonDocument.Parse(resumed.ArgumentsJson);
        Assert.True(WorkCompletionRequest.TryParse(parsed.RootElement, [], out var result, out _, out _));
        Assert.Equal(summary, WorkCompletionRequest.Summary(result));
    }

    [Theory]
    [InlineData(TriggerKind.ManualInvocation)]
    [InlineData(TriggerKind.ScheduledOccurrence)]
    public void Maximum_completion_fits_at_the_exact_reserved_boundary(TriggerKind kind)
    {
        ModelMessage[] Seed(int length) => [new(ModelRole.User, new string('x', length))];
        var reserve = DurableToolCallCheckpoint.CompletionReserve(kind);
        var overhead = Encoding.UTF8.GetByteCount(DurableToolCallCheckpoint.Write(Seed(0)));
        var seed = Seed(WorkLimits.MaxCheckpointBytes - reserve - 64 - overhead);
        Assert.True(DurableToolCallCheckpoint.TryWriteWithReserve(seed, false, null, reserve, out _));
        var args = JsonSerializer.Serialize(new { summary = new string('\u0800', 2000), attentionRequired = true, outcome = "AttentionRequested" });
        var terminal = new ModelMessage(ModelRole.Assistant, "", ToolCalls:
            [new(DurableToolCallCheckpoint.CompletionCallId, ToolCatalog.WorkComplete, args)]);
        Assert.True(DurableToolCallCheckpoint.TryWrite(seed.Append(terminal).ToArray(), false, null, out var payload));
        Assert.Equal(WorkLimits.MaxCheckpointBytes - 64, Encoding.UTF8.GetByteCount(payload));
    }

    private static ModelMessage[] NearCapacity(TriggerKind kind)
    {
        ModelMessage[] Build(int length) => [new(ModelRole.User, "Inspect current evidence."),
            new(ModelRole.Assistant, "", ToolCalls: [new("prior-read", ToolCatalog.WebFetch, "{}")]),
            new(ModelRole.Tool, JsonSerializer.Serialize(new { text = new string('x', length), truncated = false }), ToolCallId: "prior-read", Name: ToolCatalog.WebFetch)];
        var available = WorkLimits.MaxCheckpointBytes - DurableToolCallCheckpoint.CompletionReserve(kind)
            - DurableToolCallCheckpoint.FinishRequiredReserve() - 64 - Encoding.UTF8.GetByteCount(DurableToolCallCheckpoint.Write(Build(0)));
        return Build(available);
    }

    private sealed class CapacityModel(string summary, string? outcome, bool write) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var finish = request.Messages.Any(m => m.Role == ModelRole.Tool && m.Text.Contains("finish_required"));
            var call = finish ? new ModelToolCall("finish", ToolCatalog.WorkComplete, outcome is null
                ? JsonSerializer.Serialize(new { summary, attentionRequired = false, outcome = "ActionCompleted" })
                : JsonSerializer.Serialize(new { summary, attentionRequired = false, outcome }))
                : write ? new ModelToolCall("write", ToolCatalog.HttpRequest, """{"method":"POST","url":"https://example.test/note","body":"note"}""")
                : new ModelToolCall("read", ToolCatalog.WebFetch, """{"url":"https://example.test/doc"}""");
            yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls);
            await Task.CompletedTask;
        }
    }

    private sealed class Fetcher(string text) : IPublicWebFetcher
    {
        public int Calls { get; private set; }
        public ValueTask<PublicWebFetchResult> FetchAsync(PublicWebFetchRequest request, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(new PublicWebFetchResult(request.Url.AbsoluteUri, "text/plain", text, false, null, null));
        }
    }

    private sealed class Writer : IHttpRequestClient
    {
        public int Calls { get; private set; }
        public ValueTask<HttpToolResponse> SendAsync(HttpToolRequest request, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(new HttpToolResponse(200, request.Url.AbsoluteUri, "text/plain",
                new string('x', 80_000), false, true, null, null, null));
        }
    }

    private sealed class ReadModel(int reads, bool oversizedArguments = false, bool write = false, bool thought = false) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var count = request.Messages.Count(m => m.Role == ModelRole.Tool);
            var call = count >= reads
                ? new ModelToolCall("finish", ToolCatalog.WorkComplete, thought
                    ? """{"summary":"Read the document.","attentionRequired":false,"outcome":"NoAction"}"""
                    : """{"summary":"Read the document.","attentionRequired":false,"outcome":"ActionCompleted"}""")
                : write ? new ModelToolCall("write", ToolCatalog.HttpRequest, """{"method":"POST","url":"https://example.test/note","body":"note"}""")
                : new ModelToolCall($"read-{count}", ToolCatalog.WebFetch,
                    JsonSerializer.Serialize(new { url = "https://example.test/doc", extra = oversizedArguments ? new string('x', 70_000) : "" }));
            yield return new ModelToolCallEvent(call);
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            await Task.CompletedTask;
        }
    }

    private sealed class SimulatedCrash : Exception;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"agent-core-checkpoint-{Guid.NewGuid():N}.db");
        private Factory factory = null!;
        private readonly FakeTimeProvider time = new(DateTimeOffset.Parse("2026-10-06T06:00:00Z"));
        public DateTimeOffset Now => time.GetUtcNow();
        public Guid Generation { get; private set; } = Guid.NewGuid();
        public IWorkItemStore Store { get; private set; } = null!;
        public WorkItem Running { get; private set; } = null!;
        public List<WorkCheckpoint> Checkpoints { get; } = [];
        public static async Task<Fixture> CreateAsync(WorkSourceKind sourceKind = WorkSourceKind.Schedule)
        {
            var f = new Fixture();
            f.factory = new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={f.path};Pooling=False").Options);
            await new SqliteMemoryStore(f.factory, f.time).EnsureCreatedAsync();
            f.Store = new SqliteWorkItemStore(f.factory);
            var item = WorkItem.Create(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()),
                new(Guid.NewGuid(), sourceKind, null, null, null, "checkpoint-test", f.Now, f.Now,
                    "{}", "general-assistant", 11, "Test"), new("synthetic", "synthetic", "scripted", null), 3, f.Now);
            await f.Store.CreateAsync(item);
            f.Running = item;
            await f.ClaimAsync();
            return f;
        }

        public async Task ClaimAsync()
        {
            Generation = Guid.NewGuid();
            Running = (await Store.TryClaimAsync(Running.WorkItemId, Generation, Now, Now.AddMinutes(1)))!;
        }

        public async Task RecoverAsync()
        {
            Store = new SqliteWorkItemStore(factory);
            time.Advance(TimeSpan.FromMinutes(2));
            await Store.RecoverExpiredClaimsAsync(Now);
            await ClaimAsync();
        }

        public Task<DurableOccurrenceOutcome> RunAsync(SessionToolExecutor tools, ILanguageModel model,
            bool crashAfterResult = false, bool crashBeforeResult = false, bool crashAfterCompletion = false, bool crashAfterCapacityResult = false, ModelRequest? request = null, TriggerKind triggerKind = TriggerKind.ScheduledOccurrence) =>
            new DurableOccurrenceExecution(tools, time).RunAsync(Running,
                request ?? new(Guid.NewGuid(), [new(ModelRole.User, "Read the document.")]), model, Definition(), triggerKind,
                async (current, body, ct) =>
                {
                    var hasResult = DurableToolCallCheckpoint.TryRead(body, out var messages) && messages!.Any(m => m.Role == ModelRole.Tool);
                    if (hasResult && crashBeforeResult) throw new SimulatedCrash();
                    Checkpoints.Add(body);
                    var saved = await Store.CheckpointAsync(current.WorkItemId, current.Revision, Generation, body, null, Now, ct);
                    if (hasResult && crashAfterResult) throw new SimulatedCrash();
                    if (crashAfterCapacityResult && messages!.Any(m => m.Role == ModelRole.Tool && m.Text.Contains("finish_required")))
                        throw new SimulatedCrash();
                    if (crashAfterCompletion && messages!.LastOrDefault()?.ToolCalls?.Any(c => c.Name == ToolCatalog.WorkComplete) == true)
                        throw new SimulatedCrash();
                    return saved;
                }, Store, Generation, Now, new SystemIdGenerator(time), CancellationToken.None).AsTask();

        private static AgentDefinition Definition() => new(1, "general-assistant", 11, new("Test", "Role", "desc", "Tone"), [], "instructions",
            new("answerNewTurn", true, true), new("balanced", false, "en", 2048), new(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new(false, "default", 1.0), new("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string, string>(),
            new(ToolAllowlist: [ToolCatalog.WebFetch, ToolCatalog.HttpRequest]));

        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
