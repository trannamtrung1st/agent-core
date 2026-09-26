using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class RoleEnvironmentTests
{
    [Fact]
    public async Task Shipped_roles_pin_distinct_consecutive_caps()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var examiner = await store.GetAsync("examiner", 1);
        var support = await store.GetAsync("customer-support", 1);
        var compliance = await store.GetAsync("compliance", 1);
        Assert.NotNull(examiner);
        Assert.NotNull(support);
        Assert.NotNull(compliance);
        Assert.Equal(1, examiner!.InitiativePolicy.ConsecutiveCap);
        Assert.Equal(2, support!.InitiativePolicy.ConsecutiveCap);
        Assert.Equal(2, support.InitiativePolicy.MaxPerSilencePeriod);
        Assert.Equal(0, compliance!.InitiativePolicy.ConsecutiveCap);
        Assert.False(RoleEnvironments.Of(examiner).AttachmentPolicy.AllowUnreadUnsupportedTypes);
        Assert.Contains("knowledge.retrieve", RoleEnvironments.Of(support).ToolList);
        Assert.DoesNotContain("process", RoleEnvironments.Of(support).ToolList);
    }

    [Fact]
    public async Task Session_keeps_pinned_version_when_role_files_gain_a_new_version()
    {
        var directory = Directory.CreateTempSubdirectory("agent-core-roles");
        try
        {
            File.Copy(Path.Combine(FindAgents(), "examiner.json"), Path.Combine(directory.FullName, "examiner.json"));
            var storeV1 = new FileAgentDefinitionStore(directory.FullName, SyntheticProviderAliases.Default);
            var manager = new SessionManager(
                storeV1,
                new InMemoryMemoryStore(),
                new DeterministicIdGenerator(
                    Enumerable.Range(1, 8).Select(index => Guid.Parse($"019944af-00e1-7000-8000-{index:D12}")),
                    [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940be01")]),
                TimeProvider.System,
                new VoiceAvailability { SpeechAdaptersResolved = true });
            var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
            Assert.Equal(1, created.Definition.Version);
            Assert.Equal(1, created.Definition.InitiativePolicy.ConsecutiveCap);

            var v2 = File.ReadAllText(Path.Combine(directory.FullName, "examiner.json"))
                .Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal)
                .Replace("maxConsecutiveProactiveTurns\": 1", "maxConsecutiveProactiveTurns\": 3", StringComparison.Ordinal);
            Directory.CreateDirectory(Path.Combine(directory.FullName, "v2"));
            File.WriteAllText(Path.Combine(directory.FullName, "v2", "examiner.json"), v2);
            var storeLater = new FileAgentDefinitionStore(directory.FullName, SyntheticProviderAliases.Default);
            var latest = await storeLater.GetAsync("examiner");
            Assert.Equal(2, latest!.Version);
            Assert.Equal(3, latest.InitiativePolicy.ConsecutiveCap);
            var pinned = await manager.GetAsync(created.SessionId);
            Assert.Equal(1, pinned.Definition.Version);
            Assert.Equal(1, pinned.Definition.InitiativePolicy.ConsecutiveCap);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Support_runtime_consumes_pinned_consecutive_cap()
    {
        var definition = await Load("customer-support");
        Assert.Equal(2, definition.InitiativePolicy.ConsecutiveCap);
        Assert.Equal(2, definition.InitiativePolicy.MaxPerSilencePeriod);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var brain = new ScriptedCapBrain(definition);
        await using var runtime = CreateRuntime(output, time, brain, definition, new ScriptedLanguageModel(["Q?", "A", "B", "C"]));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        for (var i = 0; i < 3; i++)
        {
            await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
            await runtime.WaitUntilIdleAsync();
            if (i < 2)
            {
                time.Advance(TimeSpan.FromSeconds(121));
                await runtime.WaitUntilMailboxDrainedAsync();
            }
        }

        Assert.Equal(2, output.Items.Count(item => item.Payload is ResponseStartedOutput started && started.Trigger == "LongSilence"));
    }

    [Fact]
    public async Task Support_default_brain_allows_multiple_long_silence_within_pinned_policy()
    {
        var definition = await Load("customer-support");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel(["How can I help?", "Still there?", "Need anything else?", "Following up?"]);
        await using var runtime = CreateRuntime(
            output,
            time,
            new DefaultAgentBrain(new PromptContextBuilder(), new DefaultInitiativeEvaluator(new PromptContextBuilder(), model)),
            definition,
            model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("My order is late.");
        await runtime.WaitUntilIdleAsync();
        for (var i = 0; i < 3; i++)
        {
            await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
            await runtime.WaitUntilIdleAsync();
            if (i < 2)
            {
                time.Advance(TimeSpan.FromSeconds(121));
                await runtime.WaitUntilMailboxDrainedAsync();
            }
        }

        Assert.Equal(2, output.Items.Count(item => item.Payload is ResponseStartedOutput started && started.Trigger == "LongSilence"));
    }

    [Fact]
    public async Task Compliance_zero_cap_never_speaks()
    {
        var definition = await Load("compliance");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(
            output,
            time,
            new DefaultAgentBrain(new PromptContextBuilder()),
            definition,
            new ScriptedLanguageModel());
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, output.Items.Count(item => item.Payload is ResponseStartedOutput started && started.Trigger == "LongSilence"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task Knowledge_retrieve_returns_citation_and_denies_unauthorized_tools_and_paths()
    {
        var definition = await Load("customer-support");
        var catalog = new FileApprovedKnowledgeCatalog(FindAgents());
        var service = RoleKnowledgeService.FromApprovedCatalog(catalog, TimeProvider.System);
        var document = await service.RetrieveAsync(definition, "support-order-policy");
        Assert.Equal("support-order-policy@demo", document.Citation);
        Assert.Contains("order", document.Content, StringComparison.OrdinalIgnoreCase);
        Assert.False(RolePermissions.AllowsTool(definition, "process"));
        Assert.False(RolePermissions.AllowsTool(definition, "shell"));
        var process = Assert.Throws<AgentCoreException>(() => RolePermissions.EnsureToolAllowed(definition, "process"));
        Assert.Equal("Forbidden", process.Code);
        var examiner = await Load("examiner");
        var denied = await Assert.ThrowsAsync<AgentCoreException>(() =>
            service.RetrieveAsync(examiner, "support-order-policy").AsTask());
        Assert.Equal("Forbidden", denied.Code);
        var session = Guid.Parse("019944af-00e1-7000-8000-000000000001");
        Assert.False(RolePermissions.AllowsLogicalPath("src/AgentCore.Api/Program.cs", session));
        Assert.False(RolePermissions.AllowsLogicalPath("../.env", session));
        Assert.False(RolePermissions.AllowsLogicalPath($"/workspace/{Guid.NewGuid():D}/secret", session));
        Assert.True(RolePermissions.AllowsLogicalPath("/agent/knowledge/support-order-policy", session));
    }

    [Fact]
    public async Task Model_text_does_not_grant_process()
    {
        var definition = await Load("customer-support");
        Assert.Contains("never", definition.SystemInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.False(RolePermissions.AllowsTool(definition, "process"));
        Assert.False(RolePermissions.AllowsTool(definition, "shell"));
    }

    private static async Task<AgentDefinition> Load(string id)
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return await store.GetAsync(id) ?? throw new InvalidOperationException(id);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents) && File.Exists(Path.Combine(agents, "examiner.json")))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        FakeTimeProvider time,
        IAgentBrain brain,
        AgentDefinition definition,
        ILanguageModel model)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00e2-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940be02")]);
        var store = new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now);
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            brain,
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
    }

    private sealed class ScriptedCapBrain(AgentDefinition definition) : IAgentBrain
    {
        public ValueTask<AgentDecision> DecideAsync(
            AgentContext context,
            Guid responseId,
            CancellationToken cancellationToken = default)
        {
            var builder = new PromptContextBuilder();
            if (context.Trigger.Kind == TriggerKind.UserTurn)
            {
                return ValueTask.FromResult<AgentDecision>(new Speak(builder.Build(context, responseId)));
            }

            if (context.Trigger.Kind == TriggerKind.LongSilence)
            {
                if (context.ConsecutiveProactiveSpeaks >= definition.InitiativePolicy.ConsecutiveCap)
                {
                    return ValueTask.FromResult<AgentDecision>(new RequestDeactivate("cap"));
                }

                return ValueTask.FromResult<AgentDecision>(new Speak(builder.Build(context, responseId)));
            }

            return ValueTask.FromResult<AgentDecision>(new StaySilent("no"));
        }
    }
}
