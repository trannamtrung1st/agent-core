using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Credentials;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AgentCore.Application.Tests;

public sealed class BrowserModelCompatibilityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Credential_discovery_and_wrong_target_recovery_sign_in_without_loading_any_skills()
    {
        await using var fixture = new GenericSsoFixture();
        var model = new CredentialDiscoveryModel(fixture.ApplicationOrigin);
        var result = await Trial(fixture, model, withoutSkills: true);
        Assert.True(result.DomVerified);
        Assert.True(result.Completed);
        Assert.True(result.CapabilityLoaded);
        Assert.Equal("credential_target_invalid", Assert.Single(result.FailureCodes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_sso_runs_through_owned_runtime_with_optional_malformed_recovery(bool recover)
    {
        await using var fixture = new GenericSsoFixture();
        var model = new SsoModel(fixture.ApplicationOrigin, recover);
        var result = await Trial(fixture, model);
        Assert.True(result.DomVerified);
        Assert.True(result.Completed);
        Assert.True(result.CapabilityLoaded);
        if (!recover) Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages).SelectMany(m => m.ToolCalls ?? []), c => c.Name == ToolCatalog.BrowserFind);
        Assert.Equal(recover ? 3 : 0, result.MalformedCalls);
        Assert.Equal(recover ? 1 : 0, result.BlockedCalls);
        Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.System && m.Text.Contains("Trusted Core execution facts (current"));
    }

    [Fact]
    public async Task Continued_malformed_strategy_finishes_with_blocker_and_no_login_effect()
    {
        await using var fixture = new GenericSsoFixture();
        var model = new SsoModel(fixture.ApplicationOrigin, false, blockedForever: true);
        var result = await Trial(fixture, model);
        Assert.Equal(4, result.MalformedCalls); Assert.Equal(2, result.BlockedCalls);
        Assert.Equal(3, result.RepeatedFailures); Assert.Equal(6, result.ModelRequests);
        Assert.False(result.DomVerified); Assert.Equal(0, fixture.AcceptedLogins);
        Assert.DoesNotContain(model.Requests.Last().Tools ?? [], tool => tool.Name.StartsWith("browser."));
    }

    [Fact]
    public async Task User_steer_separates_previous_protected_fill_from_current_run()
    {
        await using var fixture = new GenericSsoFixture();
        var model = new SsoModel(fixture.ApplicationOrigin, false, interrupt: true);
        var result = await Trial(fixture, model, steer: true);
        Assert.False(result.DomVerified); Assert.Equal(0, fixture.AcceptedLogins);
        var final = model.Requests.Last();
        var previous = Assert.Single(final.Messages, m => m.Role == ModelRole.System && m.Text.StartsWith("Trusted Core execution facts (previous"));
        var current = Assert.Single(final.Messages, m => m.Role == ModelRole.System && m.Text.StartsWith("Trusted Core execution facts (current"));
        Assert.Contains("Cancelled", previous.Text); Assert.Contains("userSteer", previous.Text);
        Assert.Contains("browser.fill_credential", previous.Text); Assert.Contains("succeeded", previous.Text);
        Assert.Contains("\"loadedCapabilities\":[]", current.Text);
        Assert.DoesNotContain(GenericSsoFixture.Password, previous.Text + current.Text);
    }

    [Theory]
    [InlineData("unknown-binding")]
    [InlineData("wrong-origin")]
    [InlineData("denied-identity")]
    public async Task Sso_security_failures_never_authenticate(string scenario)
    {
        await using var fixture = new GenericSsoFixture();
        var origins = scenario == "denied-identity" ? new[] { fixture.ApplicationOrigin } : new[] { fixture.ApplicationOrigin, fixture.IdentityOrigin };
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = false,
            NavigationOrigins = origins, InteractionOrigins = origins }, loggerFactory: null);
        var root = Path.Combine(Path.GetTempPath(), "sso-denial-" + Guid.NewGuid().ToString("N"));
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Guid.NewGuid(); var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            var instances = new InMemoryAgentInstanceStore();
            var definition = await CapabilityProjectionTests.Definition([ToolCatalog.BrowserNavigate, ToolCatalog.BrowserFind, ToolCatalog.BrowserFillCredential]);
            await instances.InsertAsync(new(owner, definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now));
            var store = new InMemoryCredentialStore(instances);
            var service = new CredentialService(store, store, new LocalCredentialProtector(root), instances, new SystemIdGenerator(TimeProvider.System), TimeProvider.System);
            var credential = await service.CreateAsync("Demo SSO", "Password", null,
                [scenario == "wrong-origin" ? fixture.ApplicationOrigin : fixture.IdentityOrigin], GenericSsoFixture.Password);
            await service.BindAsync(owner, credential.CredentialId, "demo-sso", 1);
            var executor = new SessionToolExecutor(browser: browser, agentInstances: instances, credentials: service, configurationGate: ToolConfigurationGates.AllowAll);
            var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner);
            async Task<string> Execute(string name, object args) => (await executor.ExecuteAsync(definition, session,
                new(Guid.NewGuid().ToString(), name, JsonSerializer.Serialize(args)), ToolLimits.MaxOutputBytes, admission: admission)).Text;
            var navigation = await Execute(ToolCatalog.BrowserNavigate, new { url = fixture.ApplicationOrigin + "/" });
            if (scenario == "denied-identity") Assert.Contains("target_denied", navigation);
            else
            {
                using var found = JsonDocument.Parse(await Execute(ToolCatalog.BrowserFind, new { target = new { by = "label", value = "Password" } }));
                var reference = found.RootElement.GetProperty("matches")[0].GetProperty("target").Deserialize<BrowserTarget>(JsonSerializerOptions.Web);
                var result = await Execute(ToolCatalog.BrowserFillCredential, new { target = reference, credentialRef = scenario == "unknown-binding" ? "absent" : "demo-sso" });
                Assert.Contains("error", result); Assert.DoesNotContain(GenericSsoFixture.Password, result);
                Assert.Equal("", await browser.ContextFor(session)!.Pages.First().GetByLabel("Password").InputValueAsync());
            }
            Assert.Equal(0, fixture.AcceptedLogins); Assert.Equal(0, fixture.ProtectedReads);
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [BrowserBenchmarkTheory]
    [InlineData("deepseek/deepseek-v4.1-flash")]
    [InlineData("openai/gpt-6-luna")]
    [InlineData("anthropic/claude-haiku-5.5")]
    [InlineData("openai/gpt-6.1-sol")]
    public async Task Three_natural_instruction_trials_per_model(string modelId)
    {
        var results = new List<TrialResult>();
        using var http = new HttpClient();
        var descriptor = ModelCatalogFactory.Real().Models.Single(m => m.ModelId == modelId);
        var transport = descriptor.Transport;
        var model = new OpenAICompatibleLanguageModel(http, new LanguageModelProviderOptions
        {
            Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/", ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
            DefaultModel = modelId, Transport = transport, Tools = descriptor.Tools, Vision = descriptor.Vision, StructuredOutput = descriptor.StructuredOutput, ReasoningEffort = descriptor.DefaultReasoningEffort, ReasoningObjectWire = true,
            Timeouts = new() { SetupSeconds = 20, StreamIdleSeconds = 60, TotalSeconds = 120 }
        });
        var firstTrial = int.TryParse(Environment.GetEnvironmentVariable("AGENTCORE_BROWSER_BENCHMARK_FIRST_TRIAL"), out var requested) && requested is >= 1 and <= 3 ? requested : 1;
        for (var trial = firstTrial; trial <= 3; trial++)
        {
            await using var fixture = new GenericSsoFixture();
            var result = await Trial(fixture, new SemanticResponseLanguageModel(model, descriptor.PreferResponseFunction), modelId: modelId);
            results.Add(result);
            // Safe metrics only: no calls/arguments, prompts, refs, secrets, page contents or URLs.
            output.WriteLine(JsonSerializer.Serialize(new { model = modelId, effort = descriptor.DefaultReasoningEffort, trial, inferenceTransport = transport.ToString(), client = trial == firstTrial ? "cold-client" : "warm-client", profile = "cold", estimatedCostUsd = EstimatedCost(modelId, result), result }));
        }
        output.WriteLine(JsonSerializer.Serialize(new { modelId, trials = results.Count,
            verifiedCompletions = results.Count(r => r.DomVerified && r.Completed && r.ReplyCompleted),
            estimatedCostPerVerifiedCompletionUsd = results.Any(r => r.DomVerified && r.Completed && r.ReplyCompleted)
                ? results.Sum(r => EstimatedCost(modelId, r)) / results.Count(r => r.DomVerified && r.Completed && r.ReplyCompleted) : (double?)null }));
        Assert.All(results, result => Assert.True(result.DomVerified && result.Completed && result.ReplyCompleted && result.CapabilityLoaded,
            "A model trial did not independently complete authenticated record retrieval; see safe trial metrics."));
    }

    // 2026-10-09 OpenRouter base-rate estimates; excludes caching, long-context tiers and routing variation.
    private static double EstimatedCost(string modelId, TrialResult result)
    {
        var (inputRate, outputRate) = modelId switch
        {
            "openai/gpt-6-luna" or "anthropic/claude-haiku-5.5" => (0.1, 0.5),
            "openai/gpt-6.1-sol" => (2.0, 10.0),
            _ => (0.3, 1.2)
        };
        return (result.InputTokens * inputRate + result.OutputTokens * outputRate) / 1_000_000;
    }

    private async Task<TrialResult> Trial(GenericSsoFixture fixture, ILanguageModel model, bool steer = false, string? modelId = null, bool withoutSkills = false)
    {
        var timer = Stopwatch.StartNew();
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = false, InteractionMode = "InteractiveDemo",
            NavigationOrigins = [fixture.ApplicationOrigin, fixture.IdentityOrigin], InteractionOrigins = [fixture.ApplicationOrigin, fixture.IdentityOrigin] }, loggerFactory: null);
        var root = Path.Combine(Path.GetTempPath(), "sso-benchmark-" + Guid.NewGuid().ToString("N"));
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
            var definition = (await new FileAgentDefinitionStore(Path.Combine(directory!.FullName, "agents"), SyntheticProviderAliases.Default).GetAsync("general-assistant", 21))!;
            if (withoutSkills) definition = definition with { Skills = [] };
            definition = definition with { Environment = definition.Environment! with
            {
                Capabilities = new("Selected", [ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot,
                    ToolCatalog.BrowserFind, ToolCatalog.BrowserClick, ToolCatalog.BrowserType, ToolCatalog.BrowserWait, ToolCatalog.BrowserFillCredential]),
                Projection = new([ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserFind, ToolCatalog.BrowserClick, ToolCatalog.BrowserType, ToolCatalog.BrowserWait])
            } };
            var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid(); var owner = Guid.NewGuid();
            var snapshot = new SessionSnapshot(1, id, 1, definition, SessionMode.Text, null, SessionStatus.Created, [], "", 0, null, null, now, now, AgentInstanceId: owner,
                ModelSelection: modelId is null ? null : new("real/sso-benchmark", "primary-llm", modelId, ModelSelectionSource.SystemDefault, null));
            var memory = new InMemoryMemoryStore(); await memory.SaveAsync(snapshot, 0);
            var instances = new InMemoryAgentInstanceStore();
            await instances.InsertAsync(new(owner, definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now), initialSkills: definition.SkillList);
            var credentials = new InMemoryCredentialStore(instances);
            var service = new CredentialService(credentials, credentials, new LocalCredentialProtector(root), instances, new SystemIdGenerator(TimeProvider.System), TimeProvider.System);
            var credential = await service.CreateAsync("Demo SSO", "Password", new Dictionary<string, string> { ["username"] = GenericSsoFixture.Email }, [fixture.IdentityOrigin], GenericSsoFixture.Password);
            await service.BindAsync(owner, credential.CredentialId, "demo-sso", 1);
            var recording = new RecordingModel(model); var sink = new CapturingSessionOutput();
            await using var runtime = SessionRuntimeFixture.Create(snapshot, recording,
                new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)), memory, sink,
                new SystemIdGenerator(TimeProvider.System), TimeProvider.System, NullLogger<SessionRuntime>.Instance,
                tools: new SessionToolExecutor(browser: browser, agentInstances: instances, credentials: service, configurationGate: ToolConfigurationGates.AllowAll), browserLease: browser);
            await runtime.AttachAsync();
            // Identical task, no tool names, targeting hints, call order or secret value.
            Assert.True(await runtime.SubmitUserTextAsync($"Open {fixture.ApplicationOrigin}/. Sign in as {GenericSsoFixture.Email} with the saved Demo SSO credential and report the status of record AC-1042. Use only this demo site and its sign-in page."));
            if (steer)
            {
                await ((SsoModel)model).Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(await runtime.SubmitUserTextAsync("Explain what you did before I stopped you."));
            }
            var timedOut = false;
            try { await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromMinutes(4)); }
            catch (TimeoutException) { timedOut = true; }
            var replyCompleted = !timedOut && !sink.Items.Any(i => i.Payload is ErrorOutput)
                && runtime.Snapshot.Entries.Any(e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed);
            var completed = replyCompleted && runtime.Snapshot.Entries.Any(e => e.Role == ConversationRole.Assistant
                && e.Status == EntryStatus.Completed && e.Text.Contains("Ready for inspection", StringComparison.OrdinalIgnoreCase));
            var page = browser.ContextFor(id)?.Pages.FirstOrDefault();
            var dom = page is not null && page.Url == fixture.ApplicationOrigin + "/record" && fixture.AcceptedLogins == 1 && fixture.ProtectedReads > 0
                && await page.GetByRole(Microsoft.Playwright.AriaRole.Status).InnerTextAsync() == "Authenticated: AC-1042 is Ready for inspection.";
            var runs = await SessionRuntimeFixture.RunsForAsync(runtime);
            if (withoutSkills)
            {
                Assert.All(runs, run => Assert.Empty(run.ActiveSkillKeys));
                Assert.DoesNotContain(recording.Calls, call => call.Name == ToolCatalog.SkillsLoad);
                Assert.DoesNotContain(recording.Requests.SelectMany(r => r.Messages), PromptContextBuilder.IsActiveSkillSystem);
            }
            AgentRunToolCallCheckpoint.TryRead(runs.OrderByDescending(r => r.CreatedAtUtc).FirstOrDefault()?.Checkpoint, out var durable);
            var receipts = (durable ?? recording.Requests.SelectMany(r => r.Messages).ToArray()).Where(m => m.Role == ModelRole.Tool).DistinctBy(m => m.ToolCallId).ToArray();
            string? Error(ModelMessage message) { using var json = JsonDocument.Parse(message.Text); return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null; }
            Assert.DoesNotContain(GenericSsoFixture.Password, string.Join('\n', recording.Requests.SelectMany(r => r.Messages).Select(m => m.Text)));
            Assert.DoesNotContain(recording.Calls, c => c.ArgumentsJson.Contains(GenericSsoFixture.Password));
            string? TargetValidation(ModelToolCall call)
            {
                try { using var args = JsonDocument.Parse(call.ArgumentsJson); return BrowserToolArguments.InvalidTargetReason(args.RootElement); }
                catch (JsonException) { return "malformed_json"; }
            }
            output.WriteLine("Trial diagnostics: " + JsonSerializer.Serialize(new { requests = recording.Requests.Count, calls = recording.Calls.Select(c => c.Name), targetValidation = recording.Calls.Where(c => c.Name.StartsWith("browser.")).Select(TargetValidation).Where(reason => reason is not null), errors = sink.Items.Select(i => i.Payload).OfType<ErrorOutput>().Select(e => new { e.Code, e.FailureReason }), receipts = receipts.Select(r => new { r.Name, error = Error(r) }), dom, completed }));
            var malformed = receipts.Count(r => Error(r) is "invalid" or "invalid_target" or "ValidationError" or "invalid_tool_strategy_blocked");
            return new(recording.Requests.Count, recording.Calls.Count, receipts.Count(r => Error(r) is null), malformed,
                receipts.Count(r => Error(r) == "invalid_tool_strategy_blocked"),
                receipts.Count(r => { using var json = JsonDocument.Parse(r.Text); return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("invalidCallRecovery", out var recovery) && recovery.GetProperty("attempt").GetInt32() >= 2; }),
                runs.Any(r => r.LoadedCapabilityIds.Contains(ToolCatalog.BrowserFillCredential)), dom, completed, timedOut, Math.Round(timer.Elapsed.TotalSeconds, 1),
                recording.Calls.Count(c => c.Name == ToolCatalog.BrowserFind),
                receipts.Where(r => r.Name?.StartsWith("browser.") == true).Select(r => System.Text.Encoding.UTF8.GetByteCount(r.Text)).DefaultIfEmpty().Max(),
                receipts.Select(Error).Where(e => e is not null).Distinct().ToArray()!, replyCompleted, recording.InputTokens, recording.OutputTokens);
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed record TrialResult(int ModelRequests, int ToolCalls, int ValidCalls, int MalformedCalls, int BlockedCalls, int RepeatedFailures,
        bool CapabilityLoaded, bool DomVerified, bool Completed, bool TimedOut, double ElapsedSeconds,
        int FindCalls, int MaxBrowserReceiptBytes, string?[] FailureCodes, bool ReplyCompleted, int InputTokens, int OutputTokens);

    private sealed class RecordingModel(ILanguageModel model) : ILanguageModel
    {
        internal int InputTokens { get; private set; }
        internal int OutputTokens { get; private set; }
        internal List<ModelToolCall> Calls { get; } = [];
        internal List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities => model.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await foreach (var item in model.GenerateAsync(request, ct))
            { if (item is ModelToolCallEvent call) Calls.Add(call.Call);
                if (item is ModelCompleted completed) { InputTokens += completed.InputTokens ?? 0; OutputTokens += completed.OutputTokens ?? 0; }
                yield return item; }
        }
    }

    private sealed class CredentialDiscoveryModel(string origin) : ILanguageModel
    {
        private int _step;
        public ModelCapabilities Capabilities => new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var step = _step++;
            var environment = Assert.Single(request.Messages, m => m.Role == ModelRole.System && m.Text.StartsWith(PromptContextBuilder.ToolEnvironmentPrefix)).Text;
            if (step == 0)
            {
                Assert.Contains("- browser.fill_credential: Fill an existing-account password input", environment);
                Assert.DoesNotContain(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserFillCredential);
            }
            if (step == 3)
            {
                using var receipt = JsonDocument.Parse(request.Messages.Last(m => m.Role == ModelRole.Tool).Text);
                Assert.Equal("load_matched", receipt.RootElement.GetProperty("outcome").GetString());
                Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserFillCredential);
                Assert.DoesNotContain("- browser.fill_credential:", environment); // No stale on-demand entry after loading.
            }
            if (step == 5)
            {
                using var receipt = JsonDocument.Parse(request.Messages.Last(m => m.Role == ModelRole.Tool).Text);
                Assert.Equal("credential_target_invalid", receipt.RootElement.GetProperty("error").GetString());
                Assert.True(receipt.RootElement.GetProperty("capabilitySupported").GetBoolean());
                Assert.False(receipt.RootElement.GetProperty("effectAttempted").GetBoolean());
                Assert.Contains("password input", receipt.RootElement.GetProperty("nextStep").GetString());
            }
            (string Name, object Args) action = step switch
            {
                0 => (ToolCatalog.BrowserNavigate, new { url = origin + "/" }),
                1 => (ToolCatalog.BrowserType, new { target = new BrowserTarget("label", "Email"), text = GenericSsoFixture.Email }),
                2 => (ToolCatalog.CapabilitiesLoad, new { query = "sign in with saved password", limit = 1 }),
                3 => (ToolCatalog.CredentialsList, new { }),
                4 => (ToolCatalog.BrowserFillCredential, new { target = new BrowserTarget("label", "Email"), credentialRef = "demo-sso" }),
                5 => (ToolCatalog.BrowserSnapshot, new { }),
                6 => (ToolCatalog.BrowserFillCredential, new { target = new BrowserTarget("label", "Password"), credentialRef = "demo-sso" }),
                7 => (ToolCatalog.BrowserClick, new { target = new BrowserTarget("role", "button", "Sign in") }),
                8 => (ToolCatalog.BrowserSnapshot, new { }),
                _ => ("", new { })
            };
            if (action.Name.Length == 0)
            {
                Assert.Contains("Ready for inspection", request.Messages.Last(m => m.Role == ModelRole.Tool).Text);
                yield return new ModelSemanticResponseReady(new("AC-1042 is Ready for inspection.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            Assert.Contains(request.Tools ?? [], t => t.Name == action.Name);
            yield return new ModelToolCallEvent(new("credential-discovery-" + step, action.Name, JsonSerializer.Serialize(action.Args)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class SsoModel(string origin, bool recover, bool interrupt = false, bool blockedForever = false) : ILanguageModel
    {
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _step;
        internal List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities => new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); Requests.Add(request); var step = _step++;
            if (request.Messages.Last(m => m.Role == ModelRole.User).Text.StartsWith("Explain"))
            {
                yield return new ModelSemanticResponseReady(new("The previous run filled the protected field; sign-in was not confirmed.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (blockedForever && step == 5)
            {
                Assert.DoesNotContain(request.Tools ?? [], tool => tool.Name.StartsWith("browser."));
                yield return new ModelSemanticResponseReady(new("The repeated discovery strategy was blocked after invalid arguments; login was not completed.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (interrupt && step == 5)
            {
                Ready.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            var plan = step == 0 ? (ToolCatalog.BrowserNavigate, (object)new { url = origin + "/" })
                : (recover && step is >= 1 and <= 3 || blockedForever && step is >= 1 and <= 4) ? (ToolCatalog.BrowserFind, new { role = "textbox", label = "Email" })
                : (step - (recover ? 3 : 0)) switch
                {
                    1 => (ToolCatalog.BrowserType, new { target = new BrowserTarget("label", "Email"), text = GenericSsoFixture.Email }),
                    2 => (ToolCatalog.CapabilitiesLoad, new { query = "browser.fill_credential", limit = 1 }),
                    3 => (ToolCatalog.CredentialsList, new { }),
                    4 => (ToolCatalog.BrowserFillCredential, new { target = new BrowserTarget("label", "Password"), credentialRef = "demo-sso" }),
                    5 => (ToolCatalog.BrowserClick, new { target = new BrowserTarget("role", "button", "Sign in") }),
                    6 => (ToolCatalog.BrowserSnapshot, new { }),
                    _ => ("", new { })
                };
            if (plan.Item1 == "")
            {
                Assert.Contains("Ready for inspection", results.Last().Text);
                yield return new ModelSemanticResponseReady(new("AC-1042 is Ready for inspection.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            Assert.Contains(request.Tools!, tool => tool.Name == plan.Item1);
            yield return new ModelToolCallEvent(new("sso-" + step, plan.Item1, JsonSerializer.Serialize(plan.Item2)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}

public sealed class BrowserBenchmarkTheoryAttribute : TheoryAttribute
{
    public BrowserBenchmarkTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_BROWSER_BENCHMARK_LIVE") != "1") Skip = "Explicit paid browser benchmark opt-in required.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) Skip = "OpenRouter key required for live comparison.";
    }
}
