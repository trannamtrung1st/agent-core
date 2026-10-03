using AgentCore.Application.Agents;
using AgentCore.Application.Connections;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Connections;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Tests;

public sealed class ApplicationConnectionTests
{
    private const string Password = "p9-password-value";
    private const string Cookie = "p9-cookie-value";
    private const string Token = "p9-token-value";
    private const string ProfilePath = "/tmp/browser-profiles/secret";

    [Fact]
    public async Task Connection_is_per_instance_and_connected_only_past_the_login_wall()
    {
        var logs = new List<string>();
        var browser = new ScriptedConnectionBrowser();
        var store = new InMemoryApplicationConnectionStore();
        var service = new ApplicationConnectionService(
            store,
            new SystemIdGenerator(TimeProvider.System),
            TimeProvider.System,
            browser: browser,
            logger: new CollectingLogger<ApplicationConnectionService>(logs));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        browser.Observation = Page("http://127.0.0.1:5088/login", BrowserInterventionKind.AuthenticationRequired);

        var connecting = await service.ConnectAsync(
            first,
            "Store",
            $"http://127.0.0.1:5088/admin?password={Password}");
        Assert.Equal(new Uri("http://127.0.0.1:5088/admin"), browser.Navigated);
        Assert.Equal(first, browser.BoundAgent);
        var other = await service.ConnectAsync(second, "Other", "http://127.0.0.1:5099/");

        Assert.Equal(ApplicationConnectionStatus.Connecting, connecting.Status);
        Assert.Equal("http://127.0.0.1:5088", connecting.BaseUrl);
        Assert.Equal(["http://127.0.0.1:5088"], connecting.TrustedOrigins);
        Assert.Equal(first, connecting.ProfileKey);
        Assert.Equal(second, other.ProfileKey);
        Assert.NotEqual(connecting.ConnectionId, other.ConnectionId);
        Assert.Equal(new Uri("http://127.0.0.1:5099/admin"), browser.Navigated);
        Assert.Equal(second, browser.BoundAgent);
        Assert.Equal(ApplicationConnectionDetails.SignInRequired, connecting.StatusDetail);
        var stillSigningIn = await service.MarkConnectedAsync(first);
        Assert.Equal(ApplicationConnectionStatus.Connecting, stillSigningIn.Status);

        browser.Observation = Page("http://127.0.0.1:5088/admin");
        var connected = await service.MarkConnectedAsync(first);
        Assert.Equal(ApplicationConnectionStatus.Connected, connected.Status);
        Assert.Null(connected.StatusDetail);
        await service.RequireActiveAsync(first);

        await browser.CloseAsync(connected.ConnectionId);
        Assert.Equal(1, browser.CloseCalls);
        Assert.Empty(browser.ResetIds);
        Assert.Equal(ApplicationConnectionStatus.Connected, (await store.GetByAgentAsync(first))!.Status);

        browser.Observation = Page("http://127.0.0.1:5088/login", BrowserInterventionKind.AuthenticationRequired);
        var needsSignIn = await service.ApplyObservationAsync(first);
        Assert.Equal(ApplicationConnectionStatus.NeedsReauthentication, needsSignIn.Status);
        var denied = await Assert.ThrowsAsync<AgentCoreException>(() => service.RequireActiveAsync(first).AsTask());
        Assert.Equal(403, denied.StatusCode);

        browser.Observation = Page("http://127.0.0.1:5088/admin");
        var again = await service.ReauthenticateAsync(first);
        Assert.Equal(ApplicationConnectionStatus.Connected, again.Status);

        var revoked = await service.RevokeAsync(first);
        Assert.Equal(ApplicationConnectionStatus.NotConnected, revoked.Status);
        Assert.Empty(browser.ResetIds);
        var revokedDenied = await Assert.ThrowsAsync<AgentCoreException>(() => service.RequireActiveAsync(first).AsTask());
        Assert.Equal(403, revokedDenied.StatusCode);
        var missing = await Assert.ThrowsAsync<AgentCoreException>(() => service.RequireActiveAsync(Guid.NewGuid()).AsTask());
        Assert.Equal(404, missing.StatusCode);

        var reset = await service.ResetProfileAsync(second);
        Assert.Equal(ApplicationConnectionStatus.NotConnected, reset.Status);
        Assert.Equal(ApplicationConnectionDetails.ProfileReset, reset.StatusDetail);
        Assert.Equal([second], browser.ResetIds);
        Assert.Equal(ApplicationConnectionStatus.NotConnected, (await store.GetByAgentAsync(first))!.Status);
        Assert.NotNull(await store.GetByAgentAsync(second));

        var prompt = ApplicationConnectionPrompt.Format(await store.GetByAgentAsync(first));
        var request = new PromptContextBuilder().Build(
            new AgentContext(
                SampleDefinitions.Examiner,
                [],
                string.Empty,
                null,
                SessionMode.Text,
                null,
                false,
                null,
                new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hello"),
                ApplicationConnectionStatus: prompt),
            Guid.NewGuid());
        var environment = string.Join('\n', request.Messages.Select(message => message.Text));
        var rendered = string.Join(
            '\n',
            prompt,
            environment,
            string.Join('\n', logs),
            connecting.BaseUrl,
            connecting.StatusDetail,
            reset.StatusDetail);
        AssertNoSecrets(rendered);
        Assert.Contains("Application connection:", prompt, StringComparison.Ordinal);
        Assert.Contains("name: Store", prompt, StringComparison.Ordinal);
        Assert.Contains("kind: nopCommerce", prompt, StringComparison.Ordinal);
        Assert.Contains("connected: false", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("nopCommerce connected:", prompt, StringComparison.Ordinal);
        Assert.Contains("trusted origin: http://127.0.0.1:5088", prompt, StringComparison.Ordinal);
        Assert.Contains("Use this origin exactly, including its port.", prompt, StringComparison.Ordinal);
        Assert.Contains("name: Store", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_uses_display_name_and_kind()
    {
        var prompt = ApplicationConnectionPrompt.Format(new ApplicationConnection(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "crm",
            "Example CRM",
            "https://crm.example",
            ["https://crm.example"],
            ApplicationConnectionStatus.NeedsReauthentication,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null));
        Assert.Contains("name: Example CRM", prompt, StringComparison.Ordinal);
        Assert.Contains("kind: crm", prompt, StringComparison.Ordinal);
        Assert.Contains("connected: true", prompt, StringComparison.Ordinal);
        Assert.Contains("authenticated: false", prompt, StringComparison.Ordinal);
        Assert.Contains("available: false", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("nopCommerce", prompt, StringComparison.Ordinal);
        Assert.Equal(string.Empty, ApplicationConnectionPrompt.Format(null));
    }

    [Fact]
    public void Page_kind_does_not_copy_form_values()
    {
        var kind = ApplicationConnectionPageKind.Classify(
            Page(
                "http://127.0.0.1:5088/login",
                BrowserInterventionKind.AuthenticationRequired,
                $"{Password} {Cookie} {Token} {ProfilePath}"),
            ["http://127.0.0.1:5088"]);
        Assert.Equal(ApplicationConnectionPageKind.Login, kind);
        AssertNoSecrets(kind);

        var human = ApplicationConnectionPageKind.Classify(
            Page("http://127.0.0.1:5088/challenge", BrowserInterventionKind.HumanVerificationRequired, Password),
            ["http://127.0.0.1:5088"]);
        Assert.Equal(ApplicationConnectionPageKind.HumanVerification, human);

        var application = ApplicationConnectionPageKind.Classify(
            Page("http://127.0.0.1:5088/admin"),
            ["http://127.0.0.1:5088"]);
        Assert.Equal(ApplicationConnectionPageKind.Application, application);

        var unknown = ApplicationConnectionPageKind.Classify(
            Page("http://127.0.0.1:5099/admin"),
            ["http://127.0.0.1:5088"]);
        Assert.Equal(ApplicationConnectionPageKind.Unknown, unknown);
    }

    [Fact]
    public async Task Credential_in_the_base_url_is_rejected_without_echoing_it()
    {
        var service = new ApplicationConnectionService(
            new InMemoryApplicationConnectionStore(),
            new SystemIdGenerator(TimeProvider.System),
            TimeProvider.System);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            service.ConnectAsync(Guid.NewGuid(), "Store", $"http://owner:{Password}@127.0.0.1:5088/").AsTask());
        Assert.Equal(400, error.StatusCode);
        AssertNoSecrets(error.Message);
    }

    private static BrowserObservation Page(
        string url,
        BrowserInterventionKind intervention = BrowserInterventionKind.None,
        string visibleText = "") =>
        new(url, "title", visibleText, false, [], intervention);

    private static void AssertNoSecrets(string? text)
    {
        Assert.NotNull(text);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Cookie, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ProfilePath, text, StringComparison.Ordinal);
        Assert.DoesNotContain("browser-profiles", text, StringComparison.Ordinal);
    }

    private sealed class ScriptedConnectionBrowser : IBrowserSession, IBrowserProfileBinding
    {
        public BrowserObservation? Observation { get; set; }

        public Uri? Navigated { get; private set; }

        public Guid? BoundAgent { get; private set; }

        public List<Guid> ResetIds { get; } = [];

        public int CloseCalls { get; private set; }

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(true, true, BrowserInteractionMode.InteractiveDemo, []);

        public void BindSession(Guid sessionId, Guid? agentInstanceId) => BoundAgent = agentInstanceId;

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Observation is null ? null : new Uri(Observation.Url));

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default)
        {
            Navigated = request.Url;
            return ValueTask.FromResult(new BrowserOperationResult(null, Observation));
        }

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BrowserOperationResult(null, Observation));

        public ValueTask<BrowserOperationResult> ActAsync(BrowserActRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BrowserOperationResult("unsupported_operation", Observation));

        public ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            return ValueTask.FromResult(new BrowserCloseResult("closed"));
        }

        public ValueTask ResetPersistentProfileAsync(Guid agentInstanceId, CancellationToken cancellationToken = default)
        {
            ResetIds.Add(agentInstanceId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CollectingLogger<T>(List<string> messages) : ILogger<T>
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
            if (exception is not null)
            {
                messages.Add(exception.ToString());
            }
        }
    }
}
