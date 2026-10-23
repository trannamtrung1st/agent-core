using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Email;

namespace AgentCore.Application.Tests;

public sealed class EmailToolTests
{
    [Fact]
    public void Email_tools_are_hidden_when_provider_is_unavailable()
    {
        var gate = ToolConfigurationGates.From(tool =>
            tool is not (
                ToolCatalog.EmailSearch
                or ToolCatalog.EmailRead
                or ToolCatalog.EmailCreateDraft
                or ToolCatalog.EmailSend));
        var definition = Definition(
            4,
            [ToolCatalog.EmailSearch, ToolCatalog.EmailRead, ToolCatalog.EmailCreateDraft, ToolCatalog.EmailSend]);
        var context = Context(definition, modelSupportsTools: true);
        Assert.DoesNotContain(
            ToolCatalog.AppMessageSend,
            ToolCatalog.For(definition, context, gate).Select(tool => tool.Name));
    }

    [Theory]
    [InlineData("{\"limit\":10}")]
    [InlineData("{\"query\":\"\",\"limit\":10}")]
    [InlineData("""{"query":"   ","limit":10}""")]
    public async Task Show_latest_ten_emails_runs_the_search_tool_without_a_query(string arguments)
    {
        var executor = CreateExecutor(new SyntheticEmailProvider());
        var definition = Definition(4, [ToolCatalog.EmailSearch]);
        var result = await executor.ExecuteAsync(definition, Guid.NewGuid(),
            new("latest", ToolCatalog.EmailSearch, arguments), ToolLimits.MaxOutputBytes);
        using var json = JsonDocument.Parse(result.Text);
        var items = json.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.NotEmpty(items); Assert.True(items.Length <= 10);
        var dates = items.Select(item => item.GetProperty("date").GetDateTimeOffset()).ToArray();
        Assert.Equal(dates.OrderByDescending(d => d), dates);
        var descriptor = ToolRegistry.All.Single(t => t.Name == ToolCatalog.EmailSearch);
        Assert.DoesNotContain("required", descriptor.ModelDefinition.ParametersJson);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    public async Task Non_string_email_query_is_rejected(string query)
    {
        var result = await CreateExecutor(new SyntheticEmailProvider()).ExecuteAsync(Definition(4, [ToolCatalog.EmailSearch]),
            Guid.NewGuid(), new("bad-query", ToolCatalog.EmailSearch, "{\"query\":" + query + "}"), ToolLimits.MaxOutputBytes);
        Assert.Contains("query must be a string", result.Text);
    }

    [Fact]
    public async Task Email_search_read_and_create_draft_flow()
    {
        var provider = new SyntheticEmailProvider();
        var executor = CreateExecutor(provider);
        var definition = Definition(4, [ToolCatalog.EmailSearch, ToolCatalog.EmailRead, ToolCatalog.EmailCreateDraft]);
        var sessionId = Guid.NewGuid();

        var search = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s1", ToolCatalog.EmailSearch, """{"query":"harness"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("syn-msg-harness", search.Text, StringComparison.Ordinal);

        var read = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("r1", ToolCatalog.EmailRead, """{"messageId":"syn-msg-harness"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("Synthetic harness inbox", read.Text, StringComparison.Ordinal);

        var draft = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall(
                "d1",
                ToolCatalog.EmailCreateDraft,
                """{"to":["a@example.test"],"cc":[],"bcc":[],"subject":"Hi","body":"Body"}"""),
            ToolLimits.MaxOutputBytes);
        var draftId = ExtractJsonString(draft.Text, "draftId");
        Assert.False(string.IsNullOrWhiteSpace(draftId));
    }

    [Fact]
    public async Task Email_create_draft_rejects_header_injection_in_subject_and_addresses()
    {
        var executor = CreateExecutor(new SyntheticEmailProvider());
        var definition = Definition(4, [ToolCatalog.EmailCreateDraft]);
        var subject = await executor.ExecuteAsync(
            definition,
            Guid.NewGuid(),
            new ModelToolCall(
                "d1",
                ToolCatalog.EmailCreateDraft,
                """{"to":["a@example.test"],"cc":[],"bcc":[],"subject":"Hello\nBcc: evil@example.test","body":"Body"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("invalid", subject.Text, StringComparison.OrdinalIgnoreCase);

        var address = await executor.ExecuteAsync(
            definition,
            Guid.NewGuid(),
            new ModelToolCall(
                "d2",
                ToolCatalog.EmailCreateDraft,
                """{"to":["a@example.test\nBcc: evil@example.test"],"cc":[],"bcc":[],"subject":"Hi","body":"Body"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("invalid", address.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Email_send_hash_binds_bcc()
    {
        var withoutBcc = new EmailDraftSnapshot(
            "draft-1",
            ["a@example.test"],
            [],
            [],
            "Subject",
            "Body");
        var withBcc = withoutBcc with { Bcc = ["hidden@example.test"] };
        Assert.NotEqual(
            EmailDraftNormalizer.ComputeSendActionHash(withoutBcc),
            EmailDraftNormalizer.ComputeSendActionHash(withBcc));
    }

    [Fact]
    public async Task Unconfigured_email_search_is_denied_at_execution()
    {
        var provider = new SyntheticEmailProvider();
        var gate = ToolConfigurationGates.From(tool => tool != ToolCatalog.EmailSearch);
        var executor = new SessionToolExecutor(emailProvider: provider, configurationGate: gate);
        var definition = Definition(4, [ToolCatalog.EmailSearch]);
        var result = await executor.ExecuteAsync(
            definition,
            Guid.NewGuid(),
            new ModelToolCall("s1", ToolCatalog.EmailSearch, """{"query":"x"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static SessionToolExecutor CreateExecutor(IEmailProvider provider) =>
        new SessionToolExecutor(
            emailProvider: provider,
            configurationGate: ToolConfigurationGates.AllowAll);

    private static AgentDefinition Definition(int version, IReadOnlyList<string> tools) =>
        new(
            2,
            "general-assistant",
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
            Domain.Conversation.SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hi"),
            ModelSupportsTools: modelSupportsTools);

    private static string? ExtractJsonString(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed class OutcomeEmailProvider : IEmailProvider
    {
        private readonly Queue<EmailSendResult> _sendOutcomes;
        private readonly SyntheticEmailProvider _inner = new();

        public OutcomeEmailProvider(params EmailSendResult[] sendOutcomes) =>
            _sendOutcomes = new Queue<EmailSendResult>(sendOutcomes);

        public bool IsAvailable => _inner.IsAvailable;

        public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default) =>
            _inner.SearchAsync(request, cancellationToken);

        public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(request, cancellationToken);

        public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default) =>
            _inner.CreateDraftAsync(request, cancellationToken);

        public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default) =>
            _inner.GetDraftAsync(draftId, cancellationToken);

        public ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default)
        {
            if (_sendOutcomes.Count == 0)
            {
                return _inner.SendDraftAsync(request, cancellationToken);
            }

            return ValueTask.FromResult(_sendOutcomes.Dequeue());
        }
    }

    private sealed class ThrowingSendEmailProvider(Exception exception) : IEmailProvider
    {
        private readonly SyntheticEmailProvider _inner = new();

        public bool IsAvailable => _inner.IsAvailable;

        public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default) =>
            _inner.SearchAsync(request, cancellationToken);

        public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(request, cancellationToken);

        public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default) =>
            _inner.CreateDraftAsync(request, cancellationToken);

        public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default) =>
            _inner.GetDraftAsync(draftId, cancellationToken);

        public ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default) =>
            throw exception;
    }

    private sealed class RecordingSendEmailProvider : IEmailProvider
    {
        private readonly SyntheticEmailProvider _inner = new();
        public EmailDraftSnapshot? LastApproved { get; private set; }

        public bool IsAvailable => _inner.IsAvailable;

        public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default) =>
            _inner.SearchAsync(request, cancellationToken);

        public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(request, cancellationToken);

        public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default) =>
            _inner.CreateDraftAsync(request, cancellationToken);

        public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default) =>
            _inner.GetDraftAsync(draftId, cancellationToken);

        public ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default)
        {
            LastApproved = request.ApprovedDraft;
            return _inner.SendDraftAsync(request, cancellationToken);
        }
    }

    private sealed class MutatingDraftEmailProvider : IEmailProvider
    {
        private readonly SyntheticEmailProvider _inner = new();
        private readonly Dictionary<string, Func<EmailDraftSnapshot, EmailDraftSnapshot>> _mutations = new(StringComparer.Ordinal);

        public bool IsAvailable => _inner.IsAvailable;

        public void MutateDraft(string draftId, Func<EmailDraftSnapshot, EmailDraftSnapshot> mutate) =>
            _mutations[draftId] = mutate;

        public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default) =>
            _inner.SearchAsync(request, cancellationToken);

        public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(request, cancellationToken);

        public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default) =>
            _inner.CreateDraftAsync(request, cancellationToken);

        public async ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default)
        {
            var draft = await _inner.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                return null;
            }

            return _mutations.TryGetValue(draftId, out var mutate) ? mutate(draft) : draft;
        }

        public ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default) =>
            _inner.SendDraftAsync(request, cancellationToken);
    }
}
