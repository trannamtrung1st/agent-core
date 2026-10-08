using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed class AgentBrowserProfileTests
{
    [Fact]
    public async Task Reset_deletes_only_the_targeted_profile_and_close_keeps_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-core-profiles-" + Guid.NewGuid().ToString("N"));
        var messages = new List<string>();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(new CollectingLoggerProvider(messages)));
        var session = new PlaywrightBrowser(
            new BrowserOptions
            {
                Enabled = false,
                ProfileMode = nameof(BrowserProfileMode.PersistentAgent),
                ProfileRoot = root
            },
            logs);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var firstDirectory = Path.Combine(root, first.ToString("D"));
        var secondDirectory = Path.Combine(root, second.ToString("D"));
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var lockFile = firstDirectory + ".lock";
        await File.WriteAllTextAsync(lockFile, "lock");
        var sessionId = Guid.NewGuid();
        session.BindSession(sessionId, first);

        var closed = await session.CloseAsync(sessionId);
        Assert.Equal("provider_unavailable", closed.Status);
        Assert.True(Directory.Exists(firstDirectory));
        Assert.True(File.Exists(lockFile));

        await session.ResetPersistentProfileAsync(first);

        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(File.Exists(lockFile));
        Assert.True(Directory.Exists(secondDirectory));
        var rendered = string.Join('\n', messages);
        Assert.DoesNotContain(root, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(first.ToString("D"), rendered, StringComparison.Ordinal);
        Assert.Contains(messages, message => message == "Browser profile reset.");
    }

    [Fact]
    public async Task Active_instance_uses_browser_without_connection_and_archived_instance_is_denied_without_deleting_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-core-profile-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid(); var directory = Path.Combine(root, instanceId.ToString("D")); Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "sign-in"); await File.WriteAllTextAsync(marker, "saved-sign-in");
        try
        {
            var instances = new InMemoryAgentInstanceStore(); var now = DateTimeOffset.UtcNow;
            await instances.InsertAsync(new(instanceId, "general-assistant", 11, new("Test", "Secretary", "Fixture", "neutral"), AgentInstanceLifecycle.Active, now, now));
            var browser = new CountingBrowser();
            var executor = new SessionToolExecutor(browser: browser, agentInstances: instances, configurationGate: ToolConfigurationGates.AllowAll);
            var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instanceId);
            var call = new ModelToolCall("navigate", ToolCatalog.BrowserNavigate, """{"url":"http://127.0.0.1:5088/admin"}""");
            var allowed = await executor.ExecuteAsync(BrowserDefinition(), Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, admission: admission);
            Assert.DoesNotContain("forbidden", allowed.Text); Assert.Equal(1, browser.NavigateCalls); Assert.Equal(instanceId, browser.BoundAgent);
            await instances.UpdateWithExpectedRevisionAsync(new(instanceId, 1, Lifecycle: AgentInstanceLifecycle.Archived), now);
            foreach (var blocked in new[] { call, new ModelToolCall("snapshot", ToolCatalog.BrowserSnapshot, "{}"), new ModelToolCall("click", ToolCatalog.BrowserClick, """{"ref":"el_aaaaaaaaaaaaaaaaaaaaaa"}""") })
                Assert.Contains("forbidden", (await executor.ExecuteAsync(BrowserDefinition(), Guid.NewGuid(), blocked, ToolLimits.MaxOutputBytes, admission: admission)).Text);
            Assert.Equal(1, browser.NavigateCalls); Assert.Equal(0, browser.ObserveCalls); Assert.Equal(0, browser.ActCalls); Assert.True(File.Exists(marker));
            var missing = await executor.ExecuteAsync(BrowserDefinition(), Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, admission: admission with { AgentInstanceId = Guid.NewGuid() });
            Assert.Contains("forbidden", missing.Text); Assert.Equal(1, browser.NavigateCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static AgentDefinition BrowserDefinition() =>
        new(
            1,
            "secretary",
            1,
            new AgentIdentity("Morgan", "Secretary", "desc", "Tone"),
            [],
            "instructions",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 2048),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist:
            [
                ToolCatalog.BrowserNavigate,
                ToolCatalog.BrowserSnapshot,
                ToolCatalog.BrowserClick
            ]));

    private sealed class CountingBrowser : IBrowser, IBrowserProfileBinding
    {
        public BrowserProviderDescriptor Provider { get; } = new("fixture", "Test browser", new HashSet<BrowserFeature> { BrowserFeature.Navigate, BrowserFeature.Snapshot, BrowserFeature.Click, BrowserFeature.Type, BrowserFeature.Hover, BrowserFeature.Drag, BrowserFeature.FillForm, BrowserFeature.SelectOption, BrowserFeature.PressKey, BrowserFeature.Upload, BrowserFeature.FillCredential, BrowserFeature.Wait, BrowserFeature.Tabs, BrowserFeature.Screenshot, BrowserFeature.Close });
        public int NavigateCalls { get; private set; }

        public int ObserveCalls { get; private set; }

        public int ActCalls { get; private set; }

        public Guid? BoundAgent { get; private set; }

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            [],
            PolicyMode: BrowserPolicyMode.OpenWeb);

        public void BindSession(Guid sessionId, Guid? agentInstanceId) => BoundAgent = agentInstanceId;

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Uri?>(new Uri("http://127.0.0.1:5088/admin"));

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default)
        {
            NavigateCalls++;
            return ValueTask.FromResult(new BrowserOperationResult(
                null,
                new BrowserSnapshot(request.Url!.AbsoluteUri, "admin", string.Empty, false, [])));
        }

        public ValueTask<BrowserOperationResult> SnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            return ValueTask.FromResult(new BrowserOperationResult(
                null,
                new BrowserSnapshot("http://127.0.0.1:5088/admin", "admin", string.Empty, false, [])));
        }

        public ValueTask<BrowserOperationResult> InteractAsync(BrowserInteractionRequest request, CancellationToken cancellationToken = default)
        {
            ActCalls++;
            return ValueTask.FromResult(new BrowserOperationResult(
                null,
                new BrowserSnapshot("http://127.0.0.1:5088/admin", "admin", string.Empty, false, [])));
        }
    }

    private sealed class CollectingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CollectingLogger(messages);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Add(formatter(state, exception));
            }
        }
    }
}
