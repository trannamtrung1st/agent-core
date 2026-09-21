using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
namespace AgentCore.Application.Tests;

public sealed class WebPublicToolTests
{
    [Fact]
    public void General_assistant_v2_offers_web_tools_when_configured_v1_and_support_do_not()
    {
        var gate = ToolConfigurationGates.AllowAll;
        var generalV1 = Definition("general-assistant", 1, []);
        var generalV2 = Definition(
            "general-assistant",
            2,
            [ToolCatalog.WebSearch, ToolCatalog.WebFetch]);
        var support = SampleDefinitions.Support;
        var context = Context(generalV2, modelSupportsTools: true);

        Assert.Empty(ToolCatalog.For(generalV1, context, gate).Select(tool => tool.Name));
        Assert.Empty(ToolCatalog.For(support, context, gate).Select(tool => tool.Name));
        var offered = ToolCatalog.For(generalV2, context, gate).Select(tool => tool.Name).ToArray();
        Assert.Contains(ToolCatalog.WebSearch, offered);
        Assert.Contains(ToolCatalog.WebFetch, offered);
    }

    [Fact]
    public void Web_search_is_hidden_when_provider_is_not_available()
    {
        var gate = ToolConfigurationGates.From(
            tool => tool != ToolCatalog.WebSearch || false);
        var definition = Definition(
            "general-assistant",
            2,
            [ToolCatalog.WebSearch, ToolCatalog.WebFetch]);
        var context = Context(definition, modelSupportsTools: true);
        var offered = ToolCatalog.For(definition, context, gate).Select(tool => tool.Name).ToArray();
        Assert.DoesNotContain(ToolCatalog.WebSearch, offered);
        Assert.Contains(ToolCatalog.WebFetch, offered);
    }

    [Fact]
    public async Task Web_search_enforces_query_and_limit_bounds()
    {
        var search = new RecordingWebSearch();
        var executor = CreateExecutor(search);
        var definition = Definition(
            "general-assistant",
            2,
            [ToolCatalog.WebSearch, ToolCatalog.WebFetch]);
        var sessionId = Guid.NewGuid();

        var missingQuery = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("c1", ToolCatalog.WebSearch, "{}"),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("invalid", missingQuery.Text, StringComparison.OrdinalIgnoreCase);

        var longQuery = new string('q', WebToolLimits.MaxSearchQueryLength + 1);
        var tooLong = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("c2", ToolCatalog.WebSearch, $$"""{"query":"{{longQuery}}"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("invalid", tooLong.Text, StringComparison.OrdinalIgnoreCase);

        var ok = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("c3", ToolCatalog.WebSearch, """{"query":"agent core","limit":10}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("untrustedWebContent", ok.Text, StringComparison.Ordinal);
        Assert.Equal("agent core", search.LastQuery);
        Assert.Equal(10, search.LastLimit);
    }

    [Fact]
    public async Task Web_fetch_returns_untrusted_framing_and_respects_policy_denial()
    {
        var fetcher = new RecordingFetcher("Hello from the web.");
        var executor = CreateExecutor(fetch: fetcher);
        var definition = Definition(
            "general-assistant",
            2,
            [ToolCatalog.WebSearch, ToolCatalog.WebFetch]);
        var sessionId = Guid.NewGuid();

        var forbidden = await executor.ExecuteAsync(
            SampleDefinitions.Support,
            sessionId,
            new ModelToolCall("c1", ToolCatalog.WebFetch, """{"url":"https://example.test/page"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", forbidden.Text, StringComparison.OrdinalIgnoreCase);

        var ok = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("c2", ToolCatalog.WebFetch, """{"url":"https://example.test/page"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("untrustedWebContent", ok.Text, StringComparison.Ordinal);
        Assert.Contains("Hello from the web.", ok.Text, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://example.test/page"), fetcher.LastUrl);
    }

    [Fact]
    public async Task Unconfigured_web_search_is_denied_at_execution()
    {
        var search = new RecordingWebSearch();
        var gate = ToolConfigurationGates.From(tool => tool != ToolCatalog.WebSearch);
        var executor = new SessionToolExecutor(
            webSearch: search,
            configurationGate: gate);
        var definition = Definition(
            "general-assistant",
            2,
            [ToolCatalog.WebSearch, ToolCatalog.WebFetch]);
        var result = await executor.ExecuteAsync(
            definition,
            Guid.NewGuid(),
            new ModelToolCall("c1", ToolCatalog.WebSearch, """{"query":"test"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(search.LastQuery);
    }

    [Fact]
    public async Task Web_search_honors_cancellation()
    {
        var search = new CancellingWebSearch();
        var executor = CreateExecutor(search);
        var definition = Definition(
            "general-assistant",
            2,
            [ToolCatalog.WebSearch]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(
                definition,
                Guid.NewGuid(),
                new ModelToolCall("c1", ToolCatalog.WebSearch, """{"query":"x"}"""),
                ToolLimits.MaxOutputBytes,
                cts.Token));
    }

    [Fact]
    public void Web_tools_remain_read_only_in_registry()
    {
        Assert.Equal(ToolEffect.ReadOnly, ToolCatalog.EffectOf(ToolCatalog.WebSearch));
        Assert.Equal(ToolEffect.ReadOnly, ToolCatalog.EffectOf(ToolCatalog.WebFetch));
        Assert.Equal(ToolOfferRule.ConfigurationWhenRoleAllows, ToolRegistry.Get(ToolCatalog.WebSearch).OfferRule);
    }

    [Fact]
    public async Task General_assistant_v2_loads_from_agent_store()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var v1 = await store.GetAsync("general-assistant", 1);
        var v2 = await store.GetAsync("general-assistant", 2);
        Assert.NotNull(v1);
        Assert.NotNull(v2);
        Assert.Empty(RoleEnvironments.Of(v1!).ToolList);
        Assert.Contains(ToolCatalog.WebSearch, RoleEnvironments.Of(v2!).ToolList);
        Assert.Contains(ToolCatalog.WebFetch, RoleEnvironments.Of(v2!).ToolList);
    }

    private static SessionToolExecutor CreateExecutor(
        IWebSearchProvider? search = null,
        IPublicWebFetcher? fetch = null) =>
        new SessionToolExecutor(
            webSearch: search ?? new RecordingWebSearch(),
            publicWebFetcher: fetch ?? new RecordingFetcher("fixture"),
            configurationGate: ToolConfigurationGates.AllowAll);

    private static AgentDefinition Definition(string id, int version, IReadOnlyList<string> tools) =>
        new(
            1,
            id,
            version,
            new AgentIdentity("Test", "Role", "desc", "Tone"),
            [],
            "instructions",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 2048),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist: tools));

    private static AgentContext Context(AgentDefinition definition, bool modelSupportsTools) =>
        new(
            definition,
            [],
            "",
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hi"),
            ModelSupportsTools: modelSupportsTools);

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("agents directory was not found.");
    }

    private sealed class RecordingWebSearch : IWebSearchProvider
    {
        public bool IsAvailable => true;

        public string? LastQuery { get; private set; }

        public int LastLimit { get; private set; }

        public ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastQuery = request.Query;
            LastLimit = request.Limit;
            return ValueTask.FromResult(new WebSearchResult(
                [new WebSearchItem("t", "https://example.test", "s")],
                false));
        }
    }

    private sealed class CancellingWebSearch : IWebSearchProvider
    {
        public bool IsAvailable => true;

        public async ValueTask<WebSearchResult> SearchAsync(
            WebSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Expected cancellation.");
        }
    }

    private sealed class RecordingFetcher(string text = "body") : IPublicWebFetcher
    {
        public Uri? LastUrl { get; private set; }

        public ValueTask<PublicWebFetchResult> FetchAsync(
            PublicWebFetchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUrl = request.Url;
            return ValueTask.FromResult(new PublicWebFetchResult(
                request.Url.ToString(),
                "text/plain",
                text,
                false,
                null,
                null));
        }
    }
}
