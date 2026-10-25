using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

// Live effect coverage belongs to TenantSessionAccessTests.ApplicationMessages;
// unowned Runtime fixtures cannot execute tenant tools.
public sealed class ApplicationMessageTests
{
    private const string FirstText = "Still checking the order";

    [Fact]
    public void Payload_rules_reject_routing_empty_and_oversize_text()
    {
        Assert.False(ApplicationMessageAdmission.TryParseText(
            JsonDocument.Parse("""{"text":"  "}""").RootElement,
            out _,
            out var empty));
        Assert.Contains("empty", empty, StringComparison.OrdinalIgnoreCase);

        var oversize = new string('x', ApplicationMessageLimits.MaxCharacters + 1);
        Assert.False(ApplicationMessageAdmission.TryParseText(
            JsonDocument.Parse(JsonSerializer.Serialize(new { text = oversize })).RootElement,
            out _,
            out var tooLong));
        Assert.Contains("too long", tooLong, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oversize, tooLong, StringComparison.Ordinal);

        Assert.False(ApplicationMessageAdmission.TryParseText(
            JsonDocument.Parse("""{"text":"hello","sessionId":"secret"}""").RootElement,
            out _,
            out var routed));
        Assert.Contains("only text", routed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", routed, StringComparison.Ordinal);

        Assert.True(ApplicationMessageAdmission.TryParseText(
            JsonDocument.Parse("""{"text":"  hello  "}""").RootElement,
            out var text,
            out _));
        Assert.Equal("hello", text);

        var executionId = Guid.Parse("019944af-00ee-7000-8000-0000000000e1");
        Assert.False(ApplicationMessageAdmission.TryCreateEffectKey(executionId, new string('k', 170), out _));
        Assert.True(ApplicationMessageAdmission.TryCreateEffectKey(executionId, "m1", out var key));
        Assert.Equal($"v1:{executionId:N}:m1", key);
        Assert.True(key.Length <= ApplicationMessageLimits.MaxEffectKeyCharacters);
    }

    [Fact]
    public void Empty_skill_list_does_not_offer_application_messages_before_substantive_work()
    {
        var definition = Definition();
        Assert.Empty(definition.SkillList);
        var context = new AgentContext(
            definition,
            [],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hello"));
        var offered = ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll).Select(tool => tool.Name).ToArray();
        Assert.DoesNotContain(ToolCatalog.AppMessageSend, offered);
        Assert.DoesNotContain(ToolCatalog.SkillsLoad, offered);
        Assert.Contains(ToolCatalog.WorkspaceRead, offered);
        var unlocked = context with { IntermediateMessagingAllowed = true };
        Assert.Contains(
            ToolCatalog.AppMessageSend,
            ToolCatalog.For(definition, unlocked, ToolConfigurationGates.AllowAll).Select(tool => tool.Name));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.AppMessageSend,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn)));
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.AppMessageSend,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn, IntermediateMessagingAllowed: true)));
    }

    [Fact]
    public async Task Direct_tool_execution_does_not_append_an_application_message()
    {
        var executor = new SessionToolExecutor();
        var result = await executor.ExecuteAsync(
            Definition(),
            Guid.NewGuid(),
            new ModelToolCall("m1", ToolCatalog.AppMessageSend, """{"text":"Still checking the order"}"""),
            ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));
        Assert.Contains("forbidden", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstText, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_compaction_and_completion_omit_application_message_text()
    {
        var now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        var responseId = Guid.Parse("019944af-00ee-7000-8000-0000000000aa");
        var message = ApplicationEntry(Guid.Parse("019944af-00ee-7000-8000-0000000000ab"), 2, responseId, FirstText, now);
        var definition = Definition();
        var history = new List<ConversationEntry>
        {
            PromptEntry(Guid.Parse("019944af-00ee-7000-8000-0000000000a1"), 1, ConversationRole.User, "hello", now),
            message,
            PromptEntry(Guid.Parse("019944af-00ee-7000-8000-0000000000a2"), 3, ConversationRole.Assistant, "Shown", now)
        };
        var request = new PromptContextBuilder().Build(
            new AgentContext(
                definition,
                history,
                string.Empty,
                null,
                SessionMode.Text,
                null,
                false,
                null,
                new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "thanks"),
                ActiveSkillKeys: []),
            Guid.NewGuid());
        var prompt = string.Join('\n', request.Messages.Select(item => item.Text));
        Assert.Contains("hello", prompt, StringComparison.Ordinal);
        Assert.Contains("Shown", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstText, prompt, StringComparison.Ordinal);

        var page = new List<ConversationEntry> { message };
        for (var index = 0; index < CompactionPolicy.TriggerEligibleEntries; index++)
        {
            var role = index % 2 == 0 ? ConversationRole.User : ConversationRole.Assistant;
            page.Add(PromptEntry(
                Guid.Parse($"019944af-00ef-7000-8000-{index + 1:D12}"),
                index + 10,
                role,
                $"turn-{index}",
                now));
        }

        var selected = CompactionSourceSelector.Select(page, string.Empty, 0);
        Assert.True(selected.Ready);
        Assert.DoesNotContain(message.EntryId, selected.Source.Select(entry => entry.EntryId));
        Assert.DoesNotContain(FirstText, selected.PromptText, StringComparison.Ordinal);
        Assert.Contains("turn-0", selected.PromptText, StringComparison.Ordinal);

        var snapshot = new SessionSnapshot(
            1,
            Guid.Parse("019944af-00ee-7000-8000-0000000000c1"),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            history,
            string.Empty,
            0,
            null,
            null,
            now,
            now, AgentInstanceId: Guid.NewGuid());
        var evaluation = string.Join('\n', CompletionEvaluator.CreateEvaluationRequest(snapshot, now).Messages.Select(item => item.Text));
        Assert.DoesNotContain(FirstText, evaluation, StringComparison.Ordinal);
        Assert.Contains("Shown", evaluation, StringComparison.Ordinal);
    }

    private static ConversationEntry PromptEntry(Guid id, long sequence, ConversationRole role, string text, DateTimeOffset now) =>
        new(id, sequence, id, role, text, role == ConversationRole.Assistant ? id : null, EntryStatus.Completed, SessionMode.Text, text.Length, text.Length, now);

    private static ConversationEntry ApplicationEntry(Guid id, long sequence, Guid responseId, string text, DateTimeOffset now) =>
        new(
            id,
            sequence,
            null,
            ConversationRole.ApplicationMessage,
            text,
            responseId,
            EntryStatus.Completed,
            SessionMode.Text,
            0,
            text.Length,
            now,
            ApplicationMessageEffectKey: $"v1:{responseId:N}:unit");

    private static AgentDefinition Definition() =>
        SampleDefinitions.Examiner with
        {
            Voice = new VoiceConfiguration(false, "default", 1),
            ProviderPreferences = new ProviderPreferences(null, null),
            Skills = [],
            Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.WorkspaceList, ToolCatalog.WorkspaceRead])
        };

}
