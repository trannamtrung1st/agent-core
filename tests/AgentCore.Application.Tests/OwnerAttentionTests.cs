using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

public sealed class OwnerAttentionTests
{
    [Fact]
    public async Task Recipient_field_is_rejected_and_detached_messaging_stays_denied()
    {
        var definition = Definition();
        var schema = ToolRegistry.Get(ToolCatalog.WorkComplete).ModelDefinition.ParametersJson;
        Assert.DoesNotContain("recipient", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.WorkComplete,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn)));
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.WorkComplete,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(
                    true,
                    TriggerKind.ScheduledOccurrence,
                    AgentInstanceId: OwnerId)));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.BrowserNavigate,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(
                    true,
                    TriggerKind.ScheduledOccurrence,
                    AgentInstanceId: null)));
        var messaging = await new SessionToolExecutor().ExecuteAsync(
            definition,
            Guid.Empty,
            Call(ToolCatalog.AppMessageSend, """{"text":"hello"}"""),
            ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(true, TriggerKind.ScheduledOccurrence, AgentInstanceId: OwnerId));
        Assert.Contains("forbidden", messaging.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Occurrence_offers_work_complete_without_a_recipient_and_browser_requires_owner()
    {
        var brain = new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll));
        var connected = Context(trusted: true);
        var speak = Assert.IsType<Speak>(await brain.DecideAsync(connected, Guid.NewGuid()));
        Assert.Contains(speak.Request.Tools!, tool => tool.Name == ToolCatalog.WorkComplete);
        Assert.DoesNotContain(
            "recipient",
            speak.Request.Tools!.Single(tool => tool.Name == ToolCatalog.WorkComplete).ParametersJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(speak.Request.Tools!, tool => tool.Name == ToolCatalog.BrowserNavigate);
        var unconnected = Assert.IsType<Speak>(await brain.DecideAsync(Context(trusted: false), Guid.NewGuid()));
        Assert.Contains(unconnected.Request.Tools!, tool => tool.Name == ToolCatalog.WorkComplete);
        Assert.DoesNotContain(unconnected.Request.Tools!, tool => ToolCatalog.IsBrowserTool(tool.Name));
    }

    private static AgentDefinition Definition() =>
        new(
            1,
            "general-assistant",
            11,
            new AgentIdentity("Test", "Role", "desc", "Tone"),
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
                ToolCatalog.BrowserClick,
                ToolCatalog.AppMessageSend
            ]));

    private static AgentContext Context(bool trusted) =>
        new(
            Definition(),
            [],
            "",
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.ScheduledOccurrence, "review"),
            DetachedExecution: true,
            AgentInstanceId: trusted ? OwnerId : null);

    private static ModelToolCall Call(string name, string arguments) => new("call-" + name, name, arguments);
    private static readonly Guid OwnerId = Guid.NewGuid();
}
