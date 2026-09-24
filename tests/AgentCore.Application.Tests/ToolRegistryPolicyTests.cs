using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;

namespace AgentCore.Application.Tests;

public sealed class ToolRegistryPolicyTests
{
    [Fact]
    public void Registered_tools_expose_trusted_effect_metadata()
    {
        Assert.Equal(ToolEffect.ReadOnly, ToolCatalog.EffectOf(ToolCatalog.KnowledgeRetrieve));
        Assert.Equal(ToolEffect.ReadOnly, ToolCatalog.EffectOf(ToolCatalog.AttachmentsRead));
        Assert.Equal(ToolEffect.Write, ToolCatalog.EffectOf(ToolCatalog.WorkspaceWrite));
        Assert.Equal(ToolEffect.Write, ToolCatalog.EffectOf(ToolCatalog.SandboxRun));
        Assert.Equal(ToolEffect.SensitiveWrite, ToolCatalog.EffectOf(ToolCatalog.DemoSensitiveAction));
    }

    [Fact]
    public void Sensitive_execution_requires_approval_without_grant()
    {
        var definition = Definition("general-assistant", 3, [ToolCatalog.DemoSensitiveAction]);
        Assert.Equal(
            ToolPolicyDecision.RequireApproval,
            ToolPolicy.EvaluateExecution(definition, ToolCatalog.DemoSensitiveAction, ToolConfigurationGates.AllowAll));
    }

    [Fact]
    public void Unknown_tools_are_denied_at_execution()
    {
        var definition = SampleDefinitions.Support;
        var gate = ToolConfigurationGates.AllowAll;
        Assert.Equal(ToolPolicyDecision.Deny, ToolPolicy.EvaluateExecution(definition, "process", gate));
        Assert.Equal(ToolPolicyDecision.Deny, ToolPolicy.EvaluateExecution(definition, "shell", gate));
    }

    [Fact]
    public void Role_allowlist_tools_require_listing_for_execution()
    {
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(SampleDefinitions.Support, ToolCatalog.WorkspaceRead, ToolConfigurationGates.AllowAll));
    }

    [Fact]
    public void Attachments_read_uses_explicit_session_attachment_offer_rule()
    {
        var descriptor = ToolRegistry.Get(ToolCatalog.AttachmentsRead);
        Assert.Equal(ToolOfferRule.SessionAttachmentsWhenRoleAllows, descriptor.OfferRule);

        var definition = SampleDefinitions.Support;
        var withoutAttachments = Context(definition, modelSupportsTools: true, sessionAttachments: []);
        var gate = ToolConfigurationGates.AllowAll;
        Assert.False(ToolPolicy.IsOffered(definition, withoutAttachments, ToolCatalog.AttachmentsRead, gate));
        Assert.False(ToolCatalog.OffersAttachmentRead(definition, withoutAttachments, gate));

        var withAttachment = Context(
            definition,
            modelSupportsTools: true,
            sessionAttachments: [new SessionAttachmentManifestItem(Guid.NewGuid(), "a.png", "image/png", 1)]);
        Assert.True(ToolPolicy.IsOffered(definition, withAttachment, ToolCatalog.AttachmentsRead, gate));
        Assert.True(ToolCatalog.OffersAttachmentRead(definition, withAttachment, gate));
    }

    [Fact]
    public async Task Detached_occurrence_rejects_session_tools_and_trigger_writes()
    {
        var definition = Definition(
            "general-assistant",
            10,
            [
                ToolCatalog.KnowledgeRetrieve,
                ToolCatalog.WorkspaceRead,
                ToolCatalog.SandboxRun,
                ToolCatalog.TriggerScheduleOnce,
                ToolCatalog.WebFetch
            ]);
        var context = new AgentContext(
            definition,
            [],
            "",
            null,
            Domain.Conversation.SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.ApplicationEvent, """{"notice":"order shipped"}"""),
            ModelSupportsTools: true,
            DetachedExecution: true);
        var offered = ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll).Select(tool => tool.Name).ToArray();
        Assert.Contains(ToolCatalog.KnowledgeRetrieve, offered);
        Assert.Contains(ToolCatalog.WebFetch, offered);
        Assert.DoesNotContain(ToolCatalog.WorkspaceRead, offered);
        Assert.DoesNotContain(ToolCatalog.SandboxRun, offered);
        Assert.DoesNotContain(ToolCatalog.TriggerScheduleOnce, offered);

        var admission = new ToolExecutionAdmission(Detached: true, TriggerKind.ApplicationEvent);
        var executor = new SessionToolExecutor();
        var forged = await executor.ExecuteAsync(
            definition,
            Guid.Empty,
            new ModelToolCall("w1", ToolCatalog.WorkspaceRead, """{"path":"notes.txt"}"""),
            ToolLimits.MaxOutputBytes,
            admission: admission);
        Assert.Contains("Session context is required.", forged.Text, StringComparison.Ordinal);
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.TriggerScheduleOnce,
                ToolConfigurationGates.AllowAll,
                admission: admission));
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.KnowledgeRetrieve,
                ToolConfigurationGates.AllowAll,
                admission: admission));
    }

    [Fact]
    public void Non_tools_capable_models_receive_no_offered_tools()
    {
        var definition = SampleDefinitions.Support;
        var context = Context(definition, modelSupportsTools: false, sessionAttachments: []);
        Assert.Empty(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll));
    }

    [Fact]
    public async Task Executor_rechecks_policy_for_unknown_and_role_forbidden_tools()
    {
        var executor = new SessionToolExecutor();
        var sessionId = Guid.NewGuid();
        var unknown = await executor.ExecuteAsync(
            SampleDefinitions.Support,
            sessionId,
            new ModelToolCall("c1", "bash", """{"cmd":"ls"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", unknown.Text, StringComparison.OrdinalIgnoreCase);

        var examinerDenied = await executor.ExecuteAsync(
            SampleDefinitions.Examiner,
            sessionId,
            new ModelToolCall("c2", ToolCatalog.KnowledgeRetrieve, """{"identity":"support-order-policy"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", examinerDenied.Text, StringComparison.OrdinalIgnoreCase);
    }

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

    private static AgentContext Context(
        AgentDefinition definition,
        bool modelSupportsTools,
        IReadOnlyList<SessionAttachmentManifestItem> sessionAttachments) =>
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
            SessionAttachments: sessionAttachments,
            ModelSupportsTools: modelSupportsTools);
}
