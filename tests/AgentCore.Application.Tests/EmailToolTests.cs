using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

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
        var gate = ToolConfigurationGates.From(tool => tool != ToolCatalog.EmailSearch);
        var executor = new SessionToolExecutor( configurationGate:gate);
        var definition = Definition(4, [ToolCatalog.EmailSearch]);
        var result = await executor.ExecuteAsync(
            definition,
            Guid.NewGuid(),
            new ModelToolCall("s1", ToolCatalog.EmailSearch, """{"query":"x"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", result.Text, StringComparison.OrdinalIgnoreCase);
    }

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
            new ProviderPreferences("primary-stt", "primary-tts"),
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

}
