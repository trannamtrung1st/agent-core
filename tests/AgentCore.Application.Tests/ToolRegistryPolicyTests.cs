using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;

namespace AgentCore.Application.Tests;

public sealed class ToolRegistryPolicyTests
{
    [Theory]
    [InlineData("harness.inspect")]
    [InlineData("harness.knowledge.upsert")]
    [InlineData("harness.knowledge.remove")]
    [InlineData("harness.instructions.update")]
    [InlineData("harness.tool.select")]
    [InlineData("harness.tool.configure")]
    public async Task Caller_built_Harness_policy_cannot_offer_or_execute_unowned_management(string tool)
    {
        var definition = Definition("forged-harness", 1, [tool]);
        var harness = new HarnessChatContext(new(HarnessManagementMode.Managed,
            Enum.GetValues<HarnessManagementScope>(), [], []), 1, 1);
        var instance = Guid.NewGuid(); var session = Guid.NewGuid();
        var context = Context(definition, true, []) with { Harness = harness, AgentInstanceId = instance, OwnedSessionId = session };
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance,
            AgentRunId: Guid.NewGuid(), Harness: harness, OwnedSessionId: session);
        Assert.DoesNotContain(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == tool);
        Assert.Equal(ToolPolicyDecision.Deny, ToolPolicy.EvaluateExecution(definition, tool, ToolConfigurationGates.AllowAll, admission));
        var result = await new SessionToolExecutor().ExecuteAsync(definition, session,
            new ModelToolCall("forged-harness", tool, "{}"), ToolLimits.MaxOutputBytes, admission: admission);
        Assert.Contains("forbidden", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sandbox_timeout_omission_uses_selection_rather_than_a_fixed_schema_default()
    {
        using var schema = System.Text.Json.JsonDocument.Parse(ToolRegistry.Get(ToolCatalog.SandboxExec).ModelDefinition.ParametersJson);
        Assert.False(schema.RootElement.GetProperty("properties").GetProperty("timeoutSeconds").TryGetProperty("default", out _));
        SandboxCommands.Validate(new("true"));
        Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => SandboxCommands.Validate(new("true", TimeoutSeconds:0)));
    }

    [Theory]
    [InlineData(ToolCatalog.AttachmentsRead)]
    [InlineData(ToolCatalog.OfficeSpreadsheetInspect)]
    public void Spreadsheet_read_contracts_advertise_sparse_continuation_without_changing_authority(string name)
    {
        var descriptor = ToolRegistry.Get(name);
        using var schema = System.Text.Json.JsonDocument.Parse(descriptor.ModelDefinition.ParametersJson);
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal(2048, properties.GetProperty("cursor").GetProperty("maxLength").GetInt32());
        Assert.True(properties.TryGetProperty("range", out _));
        Assert.Contains("500", descriptor.ModelDefinition.Description);
        Assert.Contains("nextCursor", descriptor.ModelDefinition.Description);
        Assert.Contains("complete-workbook analysis", descriptor.ModelDefinition.Description);
        Assert.Contains("combine", descriptor.ModelDefinition.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ToolEffect.ReadOnly, descriptor.Effect);
        Assert.Equal(ToolReplaySafety.ReplaySafe, descriptor.ReplaySafety);
        Assert.Equal(ToolResourceScope.Session, descriptor.Scope);
        Assert.True(descriptor.DefinitionAuthorizable);
    }

    [Fact]
    public void Registered_tools_expose_trusted_effect_metadata()
    {
        Assert.Equal(ToolEffect.ReadOnly, ToolCatalog.EffectOf(ToolCatalog.KnowledgeRetrieve));
        Assert.Equal(ToolEffect.ReadOnly, ToolCatalog.EffectOf(ToolCatalog.AttachmentsRead));
        Assert.Equal(ToolEffect.Write, ToolCatalog.EffectOf(ToolCatalog.WorkspaceWrite));
        Assert.Equal(ToolReplaySafety.NonReplayable, ToolCatalog.ReplaySafetyOf(ToolCatalog.EmailCreateDraft));
        Assert.Equal(ToolReplaySafety.ReplaySafe, ToolCatalog.ReplaySafetyOf(ToolCatalog.KnowledgeRetrieve));
        Assert.Throws<KeyNotFoundException>(() => ToolCatalog.EffectOf("sandbox.run"));
        Assert.DoesNotContain("sandbox.run",ToolCatalog.AllKnownNames());
        Assert.Equal(ToolEffect.SensitiveWrite, ToolCatalog.EffectOf(ToolCatalog.HttpRequest));
        Assert.DoesNotContain("demo.sensitive_action", ToolCatalog.AllKnownNames());
    }

    [Fact]
    public void Unowned_sensitive_execution_requires_canonical_consent()
    {
        var definition = Definition("general-assistant", 3, [ToolCatalog.HttpRequest]);
        Assert.Equal(
            ToolPolicyDecision.RequireApproval,
            ToolPolicy.EvaluateExecution(definition, ToolCatalog.HttpRequest, ToolConfigurationGates.AllowAll));
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
                "sandbox.run",
                ToolCatalog.AutomationCreate,
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
        Assert.Equal([ToolCatalog.KnowledgeRetrieve, ToolCatalog.WebFetch, ToolCatalog.WorkComplete], offered);
        Assert.DoesNotContain(ToolCatalog.WorkspaceRead, offered);
        Assert.DoesNotContain("sandbox.run", offered);
        Assert.DoesNotContain(ToolCatalog.AutomationCreate, offered);

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
                ToolCatalog.AutomationCreate,
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
    public void Iam_inspection_is_context_only_and_requires_the_actual_tenant_session_frame()
    {
        var definition = Definition("iam-test", 1, []); var context = Context(definition, true, []);
        Assert.DoesNotContain(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.IamCheckAccess);
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var trusted = context with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new AgentCore.Domain.Conversation.TrustedExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session,
                Guid.NewGuid(), activation, activation, null, null, AgentCore.Domain.Conversation.ExecutionOrigin.UserMessage) };
        var offered = ToolCatalog.For(definition, trusted, ToolConfigurationGates.AllowAll);
        Assert.Contains(offered, t => t.Name == ToolCatalog.IamCheckAccess);
        Assert.Contains(offered, t => t.Name == ToolCatalog.IamEffectivePermissions);
        Assert.DoesNotContain(ToolCatalog.For(definition, trusted with { OwnedSessionId = Guid.NewGuid() }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.IamCheckAccess);
        Assert.Empty(ToolCatalog.For(definition, trusted with { ModelSupportsTools = false }, ToolConfigurationGates.AllowAll));
        Assert.False(ToolRegistry.Get(ToolCatalog.IamCheckAccess).DefinitionAuthorizable);
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
            2,
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
