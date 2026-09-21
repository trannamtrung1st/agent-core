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
        Assert.Empty(ToolCatalog.For(definition, context, gate).Select(tool => tool.Name));
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
    public async Task Email_send_requires_approval_and_executes_once()
    {
        EmailSendLedger.Reset();
        var provider = new SyntheticEmailProvider();
        var executor = CreateExecutor(provider);
        var definition = Definition(4, [ToolCatalog.EmailCreateDraft, ToolCatalog.EmailSend]);
        var sessionId = Guid.NewGuid();

        var draft = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall(
                "d1",
                ToolCatalog.EmailCreateDraft,
                """{"to":["a@example.test"],"cc":[],"bcc":[],"subject":"Send","body":"Ready"}"""),
            ToolLimits.MaxOutputBytes);
        var draftId = ExtractJsonString(draft.Text, "draftId")!;

        var args = JsonDocument.Parse($$"""{"draftId":"{{draftId}}"}""").RootElement;
        var prepared = await executor.PrepareEmailSendApprovalAsync(args);
        Assert.NotNull(prepared.Preparation);
        Assert.Contains("Send", prepared.Preparation!.Summary, StringComparison.Ordinal);

        var withoutGrant = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s0", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("approval_required", withoutGrant.Text, StringComparison.Ordinal);

        var grant = new ToolApprovalGrant(
            Guid.NewGuid(),
            ToolCatalog.EmailSend,
            prepared.Preparation!.ActionHash,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        var sent = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s1", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("\"outcome\":\"sent\"", sent.Text, StringComparison.OrdinalIgnoreCase);

        var duplicate = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s2", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("duplicate", duplicate.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Email_send_allows_retry_after_definite_failure_then_blocks_after_sent()
    {
        EmailSendLedger.Reset();
        var provider = new OutcomeEmailProvider(
            new EmailSendResult(EmailSendOutcome.Failed, null, "provider", "Rejected."),
            new EmailSendResult(EmailSendOutcome.Sent, "msg-1", null, null));
        var executor = CreateExecutor(provider);
        var definition = Definition(4, [ToolCatalog.EmailCreateDraft, ToolCatalog.EmailSend]);
        var sessionId = Guid.NewGuid();

        var draft = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall(
                "d1",
                ToolCatalog.EmailCreateDraft,
                """{"to":["a@example.test"],"cc":[],"bcc":[],"subject":"Retry","body":"Body"}"""),
            ToolLimits.MaxOutputBytes);
        var draftId = ExtractJsonString(draft.Text, "draftId")!;
        var args = JsonDocument.Parse($$"""{"draftId":"{{draftId}}"}""").RootElement;
        var prepared = await executor.PrepareEmailSendApprovalAsync(args);
        var grant = new ToolApprovalGrant(
            Guid.NewGuid(),
            ToolCatalog.EmailSend,
            prepared.Preparation!.ActionHash,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        var failed = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s1", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("\"outcome\":\"failed\"", failed.Text, StringComparison.OrdinalIgnoreCase);

        var sent = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s2", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("\"outcome\":\"sent\"", sent.Text, StringComparison.OrdinalIgnoreCase);

        var duplicate = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s3", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("duplicate", duplicate.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Email_send_duplicate_is_scoped_to_response_identity()
    {
        EmailSendLedger.Reset();
        var provider = new OutcomeEmailProvider(
            new EmailSendResult(EmailSendOutcome.Sent, "msg-1", null, null),
            new EmailSendResult(EmailSendOutcome.Sent, "msg-2", null, null));
        var executor = CreateExecutor(provider);
        var definition = Definition(4, [ToolCatalog.EmailCreateDraft, ToolCatalog.EmailSend]);
        var sessionId = Guid.NewGuid();

        var draft = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall(
                "d1",
                ToolCatalog.EmailCreateDraft,
                """{"to":["a@example.test"],"cc":[],"bcc":[],"subject":"Scope","body":"Body"}"""),
            ToolLimits.MaxOutputBytes);
        var draftId = ExtractJsonString(draft.Text, "draftId")!;
        var args = JsonDocument.Parse($$"""{"draftId":"{{draftId}}"}""").RootElement;
        var prepared = await executor.PrepareEmailSendApprovalAsync(args);
        var approvalId = Guid.NewGuid();
        var grantA = new ToolApprovalGrant(
            approvalId,
            ToolCatalog.EmailSend,
            prepared.Preparation!.ActionHash,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        var grantB = new ToolApprovalGrant(
            approvalId,
            ToolCatalog.EmailSend,
            prepared.Preparation!.ActionHash,
            Guid.NewGuid(),
            grantA.OperationId,
            grantA.RuntimeEpoch);

        var first = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s1", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grantA);
        Assert.Contains("\"outcome\":\"sent\"", first.Text, StringComparison.OrdinalIgnoreCase);

        var secondResponseSend = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s2", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grantB);
        Assert.Contains("\"outcome\":\"sent\"", secondResponseSend.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Email_send_allows_retry_after_indeterminate_outcome()
    {
        EmailSendLedger.Reset();
        var provider = new OutcomeEmailProvider(
            new EmailSendResult(EmailSendOutcome.Indeterminate, null, "transport", "Unknown."),
            new EmailSendResult(EmailSendOutcome.Sent, "msg-2", null, null));
        var executor = CreateExecutor(provider);
        var definition = Definition(4, [ToolCatalog.EmailCreateDraft, ToolCatalog.EmailSend]);
        var sessionId = Guid.NewGuid();

        var draft = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall(
                "d1",
                ToolCatalog.EmailCreateDraft,
                """{"to":["a@example.test"],"cc":[],"bcc":[],"subject":"Indeterminate","body":"Body"}"""),
            ToolLimits.MaxOutputBytes);
        var draftId = ExtractJsonString(draft.Text, "draftId")!;
        var args = JsonDocument.Parse($$"""{"draftId":"{{draftId}}"}""").RootElement;
        var prepared = await executor.PrepareEmailSendApprovalAsync(args);
        var grant = new ToolApprovalGrant(
            Guid.NewGuid(),
            ToolCatalog.EmailSend,
            prepared.Preparation!.ActionHash,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        var ambiguous = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s1", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("\"outcome\":\"indeterminate\"", ambiguous.Text, StringComparison.OrdinalIgnoreCase);

        var sent = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s2", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("\"outcome\":\"sent\"", sent.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Email_send_rejects_hash_mismatch_when_draft_changes()
    {
        EmailSendLedger.Reset();
        var provider = new MutatingDraftEmailProvider();
        var executor = CreateExecutor(provider);
        var definition = Definition(4, [ToolCatalog.EmailCreateDraft, ToolCatalog.EmailSend]);
        var sessionId = Guid.NewGuid();

        var draft = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall(
                "d1",
                ToolCatalog.EmailCreateDraft,
                """{"to":["a@example.test"],"cc":[],"bcc":[],"subject":"Original","body":"Body"}"""),
            ToolLimits.MaxOutputBytes);
        var draftId = ExtractJsonString(draft.Text, "draftId")!;
        var args = JsonDocument.Parse($$"""{"draftId":"{{draftId}}"}""").RootElement;
        var prepared = await executor.PrepareEmailSendApprovalAsync(args);
        Assert.NotNull(prepared.Preparation);

        provider.MutateDraft(draftId, snapshot => snapshot with { Subject = "Changed subject" });

        var grant = new ToolApprovalGrant(
            Guid.NewGuid(),
            ToolCatalog.EmailSend,
            prepared.Preparation!.ActionHash,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        var result = await executor.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("s1", ToolCatalog.EmailSend, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("stale_approval", result.Text, StringComparison.OrdinalIgnoreCase);
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
            1,
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
