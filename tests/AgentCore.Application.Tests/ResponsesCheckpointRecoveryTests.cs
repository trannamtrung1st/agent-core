using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ResponsesCheckpointRecoveryTests
{
    private const string ModelId = "openai/gpt-6-luna";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Origins = ["http://127.0.0.1:5091"];
    private static AgentDefinition Definition => SampleDefinitions.Examiner with
    {
        Voice = new(false, "default", 1), InitiativePolicy = SampleDefinitions.Examiner.InitiativePolicy with { Enabled = false },
        SystemInstructions = "This is a synthetic background counter fixture. Alpha and Beta must each be clicked exactly once. Existing successful tool receipts are authoritative; do not repeat them. Once both are done, call work.complete with summary 'Both counters verified once', attentionRequired false and outcome Response.",
        Environment = new(ToolAllowlist: [ToolCatalog.BrowserClick])
    };

    [Fact]
    public async Task Sqlite_reopen_resumes_only_the_unanswered_call_and_replays_both_results_with_reasoning()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-core-responses-restart-" + Guid.NewGuid().ToString("N"));
        try { await Prepare(root, live: false); await Resume(root, live: false); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [ResponsesRestartLiveFact("prepare")]
    public Task Paid_background_prepare_before_process_exit() => Prepare(LiveRoot(), live: true);

    [ResponsesRestartLiveFact("resume")]
    public Task Paid_background_resume_in_new_process() => Resume(LiveRoot(), live: true);

    private static string LiveRoot()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("AGENTCORE_RESPONSES_RESTART_ROOT") ?? throw new InvalidOperationException("Isolated restart root required."));
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "agent-core-responses-restart-"), root);
        return root;
    }

    private static async Task Prepare(string root, bool live)
    {
        Assert.False(Directory.Exists(root)); Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider(Now);
        var (memory, runs) = await Stores(root, clock);
        var owner = new AgentRunOwner(Guid.NewGuid(), Guid.NewGuid());
        var runId = Guid.NewGuid();
        var snapshot = AgentRunTestFixtures.Snapshot(owner, Definition, Now) with
        {
            ModelSelection = new("gpt-6-luna", "primary-llm", ModelId, ModelSelectionSource.Host, "low"),
            Origin = new(SessionOriginKind.ManualBackground, initialBackgroundAgentRunId: runId), Surfaces = SessionSurface.BackgroundWork
        };
        var input = snapshot.Entries.Single();
        var activation = new Activation(Guid.NewGuid(), snapshot.SessionId, ActivationKind.ManualBackground, [input.EntryId], input.SourceEventId,
            null, null, null, "responses-restart-fixture", Now);
        var run = AgentRun.Create(runId, owner, new(activation, Definition.Id, Definition.Version, Definition.Identity,
            Guid.NewGuid(), AgentRunOutputContract.BackgroundOutcome, configuration: new(Definition, 0, 1, [])), new("gpt-6-luna", "primary-llm", ModelId, "low"), 3, Now);
        run = (await runs.AdmitAsync(snapshot, 0, run)).Run;
        run = await runs.ApplyAsync(owner, runId, new AgentRunCommand.Claim(run.Revision, Now, Guid.NewGuid(), Now.AddSeconds(1)));
        using var http = live ? new HttpClient() : new HttpClient(new FixtureHandler(initial: true));
        var model = Model(http, live);
        var tool = ToolRegistry.All.Single(t => t.Name == ToolCatalog.BrowserClick).ModelDefinition;
        var calls = new List<ModelToolCall>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await foreach (var evt in model.GenerateAsync(new(Guid.NewGuid(), [new(ModelRole.User,
            "Call browser.click twice in this response, once for target {by:'role',value:'button',name:'Alpha'} and once for target {by:'role',value:'button',name:'Beta'}. No other calls or reply. These are two synthetic counters.")],
            MaxOutputTokens: 4096, Tools: [tool], ReasoningEffort: "low", ToolChoice: ModelToolChoice.Required), deadline.Token))
        {
            Assert.False(evt is ModelFailed, "Initial inference failed.");
            if (evt is ModelToolCallEvent call) calls.Add(call.Call);
        }
        Assert.Equal(2, calls.Count); Assert.Equal(2, calls.Select(c => c.Id).Distinct().Count());
        Assert.NotNull(calls[0].ContinuationToken); Assert.Null(calls[1].ContinuationToken);
        Assert.All(calls, call => Assert.Equal(ToolCatalog.BrowserClick, call.Name));
        ModelMessage[] batch = [new(ModelRole.Assistant, "", ToolCalls: calls)];
        run = await runs.ApplyAsync(owner, runId, new AgentRunCommand.Checkpoint(run.Revision, Now, run.Claim!.Generation,
            new(AgentRunToolCallCheckpoint.Write(batch), 2, 0, 240000), null));
        var first = calls[0];
        using var args = JsonDocument.Parse(first.ArgumentsJson);
        var hash = ToolActionHash.Compute(first.Name, args.RootElement);
        foreach (var disposition in new[] { AgentRunSideEffectDisposition.Prepared, AgentRunSideEffectDisposition.InFlight })
            run = await runs.ApplyAsync(owner, runId, new AgentRunCommand.MarkSideEffect(run.Revision, Now, run.Claim!.Generation, disposition, first.Id, hash));
        var executor = new SessionToolExecutor(browser: new CounterBrowser(root), configurationGate: ToolConfigurationGates.AllowAll);
        var result = await executor.ExecuteAsync(Definition, snapshot.SessionId, first, ToolLimits.MaxOutputBytes,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: owner.AgentInstanceId));
        Assert.DoesNotContain("\"error\"", result.Text);
        run = await runs.ApplyAsync(owner, runId, new AgentRunCommand.MarkSideEffect(run.Revision, Now, run.Claim!.Generation, AgentRunSideEffectDisposition.Succeeded, first.Id, hash));
        run = await runs.ApplyAsync(owner, runId, new AgentRunCommand.Checkpoint(run.Revision, Now, run.Claim!.Generation,
            new(AgentRunToolCallCheckpoint.Write([..batch, new(ModelRole.Tool, result.Text, ToolCallId: first.Id, Name: first.Name)]), 2, 0, 240000), null));
        run = await runs.ApplyAsync(owner, runId, new AgentRunCommand.ClearSideEffect(run.Revision, Now, run.Claim!.Generation));
        await File.WriteAllTextAsync(Path.Combine(root, "identity.json"), JsonSerializer.Serialize(new Identity(owner, snapshot.SessionId, runId)));
        SqliteConnection.ClearAllPools();
    }

    private static async Task Resume(string root, bool live)
    {
        var identity = JsonSerializer.Deserialize<Identity>(await File.ReadAllTextAsync(Path.Combine(root, "identity.json")))!;
        var clock = new FakeTimeProvider(Now.AddSeconds(2));
        var (memory, runs) = await Stores(root, clock);
        var run = (await runs.GetAsync(identity.Owner, identity.RunId))!;
        var responseId = run.ResponseId;
        Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var restored));
        var original = Assert.Single(restored!, message => message.Role == ModelRole.Assistant).ToolCalls!;
        Assert.Equal(2, original.Count); Assert.NotNull(original[0].ContinuationToken);
        Assert.Equal(original[1], Assert.Single(AgentRunToolCallCheckpoint.PendingCalls(restored!)));
        var savedResult = Assert.Single(restored!, message => message.Role == ModelRole.Tool);
        Assert.Equal(original[0].Id, savedResult.ToolCallId);
        run = await runs.ApplyAsync(identity.Owner, identity.RunId, new AgentRunCommand.Recover(run.Revision, clock.GetUtcNow()));
        run = await runs.ApplyAsync(identity.Owner, identity.RunId, new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)));
        var handler = new FixtureHandler(initial: false);
        using var http = live ? new HttpClient() : new HttpClient(handler);
        var browser = new CounterBrowser(root);
        await using var runtime = SessionRuntimeFixture.Create((await memory.LoadAsync(identity.SessionId))!, Model(http, live),
            new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)), memory,
            new CapturingSessionOutput(), new SystemIdGenerator(clock), clock, NullLogger.Instance,
            agentRuns: runs, tools: new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll));
        Assert.True(await runtime.DispatchAgentRunAsync(identity.RunId, headless: true));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromMinutes(2));
        var completed = (await runs.GetAsync(identity.Owner, identity.RunId))!;
        Assert.True(completed.Status == AgentRunStatus.Completed, $"Run failed: {completed.Failure?.Code}; {completed.Failure?.Summary}; provider requests={handler.Bodies.Count}; counters={JsonSerializer.Serialize(browser.Read())}");
        Assert.Equal(responseId, completed.ResponseId);
        Assert.Equal("Both counters verified once", completed.Result!.Text);
        var counters = browser.Read();
        Assert.Equal(1, counters["Alpha"]); Assert.Equal(1, counters["Beta"]);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var final));
        Assert.DoesNotContain(AgentRunToolCallCheckpoint.PendingCalls(final!), call => call.Name == ToolCatalog.BrowserClick);
        using var beforeCompaction = JsonDocument.Parse(savedResult.Text);
        using var afterCompaction = JsonDocument.Parse(Assert.Single(final!, message => message.Role == ModelRole.Tool && message.ToolCallId == savedResult.ToolCallId).Text);
        foreach (var property in new[] { "status", "effectAttempted", "effectConfirmedBySdk", "applicationOutcomeVerified", "url", "title" })
            Assert.Equal(beforeCompaction.RootElement.GetProperty(property).GetRawText(), afterCompaction.RootElement.GetProperty(property).GetRawText());
        foreach (var call in original)
        {
            Assert.Equal(call, Assert.Single(final!.SelectMany(m => m.ToolCalls ?? []), candidate => candidate.Id == call.Id));
            Assert.Single(final!, message => message.Role == ModelRole.Tool && message.ToolCallId == call.Id);
        }
        if (!live)
        {
            Assert.Single(handler.Bodies);
            using var sent = JsonDocument.Parse(handler.Bodies[0]);
            var input = sent.RootElement.GetProperty("input").EnumerateArray().ToArray();
            Assert.Single(input, item => item.TryGetProperty("type", out var type) && type.GetString() == "reasoning"
                && item.GetProperty("encrypted_content").GetString() == "opaque-round-signature");
            foreach (var call in original)
            {
                var replay = Assert.Single(input, item => item.TryGetProperty("call_id", out var id) && id.GetString() == call.Id && item.GetProperty("type").GetString() == "function_call");
                Assert.Equal(call.ArgumentsJson, replay.GetProperty("arguments").GetString());
                var output = Assert.Single(input, item => item.TryGetProperty("call_id", out var id) && id.GetString() == call.Id && item.GetProperty("type").GetString() == "function_call_output");
                Assert.Equal(Assert.Single(final!, message => message.Role == ModelRole.Tool && message.ToolCallId == call.Id).Text, output.GetProperty("output").GetString());
            }
        }
        SqliteConnection.ClearAllPools();
    }

    private static OpenAICompatibleLanguageModel Model(HttpClient http, bool live) => new(http, new()
    {
        BaseUrl = "https://openrouter.ai/api/v1/", DefaultModel = ModelId, Transport = ModelInferenceTransport.Responses,
        ApiKey = live ? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") : null, Tools = true,
        Timeouts = new() { SetupSeconds = 30, StreamIdleSeconds = 60, TotalSeconds = 120 }
    });
    private static async Task<(SqliteMemoryStore, SqliteAgentRunStore)> Stores(string root, TimeProvider clock)
    {
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={Path.Combine(root, "state.db")}").Options);
        var memory = new SqliteMemoryStore(factory, clock); await memory.EnsureCreatedAsync();
        return (memory, new SqliteAgentRunStore(factory, memory, new SystemDiagnosticIdSource()));
    }
    private sealed record Identity(AgentRunOwner Owner, Guid SessionId, Guid RunId);
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class FixtureHandler(bool initial) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var output = initial ? new object[] {
                new { type = "reasoning", id = "rs_1", encrypted_content = "opaque-round-signature", summary = Array.Empty<object>() },
                Call("call-alpha", "browser_click", "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"Alpha\"}}"),
                Call("call-beta", "browser_click", "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"Beta\"}}") }
                : [Call("finish", "work_complete", "{\"summary\":\"Both counters verified once\",\"attentionRequired\":false,\"outcome\":\"Response\"}")];
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(new { type = "response.completed", response = new { status = "completed", output } }) + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
        private static object Call(string id, string name, string args) => new { type = "function_call", call_id = id, name, arguments = args };
    }
    private sealed class CounterBrowser(string root) : IBrowser, IBrowserContextUse
    {
        public BrowserProviderDescriptor Provider { get; } = new("counter-fixture", "Synthetic counters", new HashSet<BrowserFeature> { BrowserFeature.Click });
        public bool IsAvailable => true;
        public BrowserHostPolicy HostPolicy { get; } = new(true, true, BrowserInteractionMode.InteractiveDemo, Origins);
        public Dictionary<string,int> Read() => File.Exists(Path.Combine(root, "counters.json")) ? JsonSerializer.Deserialize<Dictionary<string,int>>(File.ReadAllText(Path.Combine(root, "counters.json")))! : [];
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            var target = ((BrowserClick)request.Command).Target.Name!;
            Assert.Contains(target, new[] { "Alpha", "Beta" });
            var counts = Read(); counts[target] = counts.GetValueOrDefault(target) + 1;
            File.WriteAllText(Path.Combine(root, "counters.json"), JsonSerializer.Serialize(counts));
            return ValueTask.FromResult(new BrowserResult(null, new BrowserSnapshot(Origins[0], "Counters", $"{target} count={counts[target]}", false, []), EffectAttempted: true, EffectConfirmedBySdk: true));
        }
        public ValueTask<IAsyncDisposable> EnterUnattendedAsync(Guid owner, IReadOnlyList<string>? origins, CancellationToken ct = default) => ValueTask.FromResult<IAsyncDisposable>(new Lease());
        public void AdoptUnattendedFlow(Guid owner) { }
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}

public sealed class ResponsesRestartLiveFactAttribute : FactAttribute
{
    public ResponsesRestartLiveFactAttribute(string phase)
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_RESPONSES_RESTART_LIVE") != "1"
            || Environment.GetEnvironmentVariable("AGENTCORE_RESPONSES_RESTART_PHASE") != phase) Skip = "Explicit isolated live restart phase required.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) Skip = "OpenRouter credential required.";
    }
}
