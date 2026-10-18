using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Application.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantApprovalToolContractTests
{
    [Fact]
    public void Artifact_consent_binds_exact_normalized_bytes_and_rejects_model_selected_authority()
    {
        const string input = """{"operation":"publish_artifact","artifact":{"displayName":"../review.md","content":"<script>literal</script>\r\nUTF-8: thử nghiệm"}}""";
        var intent = TenantApprovalToolContract.Parse(new("file", ToolCatalog.RequestAuthorization, input));
        var parsed = TenantArtifactToolContract.Parse(intent.ArtifactArguments!);
        Assert.Equal("review.md", parsed.DisplayName); Assert.Equal("<script>literal</script>\r\nUTF-8: thử nghiệm", parsed.Content);
        var saved = TenantApprovalRules.PrepareArtifact(Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), parsed.CanonicalArguments), 1, 1);
        Assert.True(intent.Matches(saved)); Assert.False((intent with { ArtifactArguments = parsed.CanonicalArguments.Replace("literal", "changed") }).Matches(saved));
        Assert.Equal(saved.ActionHash, TenantApprovalRules.Rebuild(saved).ActionHash);
        var changed = TenantApprovalRules.PrepareArtifact(Guid.NewGuid(), saved.InstanceId, saved.ArtifactPublication!, 2, 1);
        Assert.NotEqual(saved.ActionHash, changed.ActionHash); Assert.Equal(saved.SemanticActionJson, changed.SemanticActionJson);
        foreach (var key in new[] { "tenantId", "sessionId", "artifactId", "runId", "principalId", "permission", "grantId", "expectedRevision" })
        {
            var root = JsonNode.Parse(input)!; root[key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("bad", ToolCatalog.RequestAuthorization, root.ToJsonString())));
            root = JsonNode.Parse(input)!; root["artifact"]![key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("bad", ToolCatalog.RequestAuthorization, root.ToJsonString())));
        }
        Assert.Contains("publish_artifact", TenantApprovalToolContract.Descriptor(settings: false, credentials: false, artifactPublication: true).ModelDefinition.ParametersJson);
        Assert.DoesNotContain("publish_artifact", TenantApprovalToolContract.Descriptor(settings: false, credentials: false).ModelDefinition.ParametersJson);
    }

    [Fact]
    public void Credential_metadata_intent_normalizes_only_discovery_arguments_and_never_accepts_authority_or_scope_members()
    {
        const string input = """{"operation":"inspect_credential_metadata","arguments":{"cursor":" ACCOUNT ","limit":2}}""";
        var intent = TenantApprovalToolContract.Parse(new("metadata", ToolCatalog.RequestAuthorization, input));
        Assert.Equal("{\"cursor\":\"account\",\"limit\":2}", intent.MetadataArguments);
        foreach (var key in new[] { "tenantId", "principalId", "credentialId", "bindingId", "permission", "grantId", "value", "expectedRevision" })
        {
            var root = JsonNode.Parse(input)!.AsObject(); root[key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("bad", ToolCatalog.RequestAuthorization, root.ToJsonString())));
            root = JsonNode.Parse(input)!.AsObject(); root["arguments"]![key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("bad", ToolCatalog.RequestAuthorization, root.ToJsonString())));
        }
        foreach (var args in new[] { "null", "[]", "{\"limit\":0}", "{\"limit\":101}", "{\"cursor\":\"bad/alias\"}", "{\"limit\":1,\"limit\":2}" })
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("bad", ToolCatalog.RequestAuthorization,
                "{\"operation\":\"inspect_credential_metadata\",\"arguments\":" + args + "}")));
        Assert.Contains("inspect_credential_metadata", TenantApprovalToolContract.Descriptor(settings: false, credentials: false, credentialMetadata: true).ModelDefinition.ParametersJson);
        Assert.DoesNotContain("inspect_credential_metadata", TenantApprovalToolContract.Descriptor(settings: false, credentials: false).ModelDefinition.ParametersJson);
    }
    [Fact]
    public void Credential_authorization_accepts_only_a_bound_reference_and_matches_only_that_operation_kind()
    {
        const string arguments = """{"operation":"check_credential_connection","reference":" Work-Drive "}""";
        var intent = TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, arguments));
        Assert.Equal("work-drive", intent.CredentialReference); Assert.Null(intent.Persona); Assert.Null(intent.Preferences);
        var saved = TenantApprovalRules.PrepareCredentialCheck(Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), "work-drive", 1, 1, 1, 1), 1, 1);
        Assert.True(intent.Matches(saved)); Assert.False(new TenantAuthorizationIntent(null, null).Matches(saved));
        Assert.False((intent with { CredentialReference = "another" }).Matches(saved));
        Assert.False((intent with { Persona = Persona }).Matches(saved));
        foreach (var key in new[] { "tenantId", "credentialId", "bindingId", "runId", "permission", "expectedRevision", "grantId", "persona", "preferences" })
        {
            var input = JsonNode.Parse(arguments)!.AsObject(); input[key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, input.ToJsonString())));
        }
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, arguments.Replace(" Work-Drive ", "bad/reference"))));
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, arguments[..^1] + ",\"reference\":\"other\"}")));
    }

    [Fact]
    public async Task Credential_offering_is_independent_of_Settings_and_restricts_its_schema_without_authorizing_other_operations()
    {
        var definition = await CapabilityProjectionTests.Definition();
        definition = definition with { SelfManagement = HarnessManagementPolicy.Disabled,
            Environment = definition.Environment! with { Capabilities = null, ToolAllowlist = [ToolCatalog.DriveAccounts] } };
        var instance = Guid.NewGuid(); var session = Guid.NewGuid(); var activation = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        var context = CapabilityProjectionTests.Context(definition) with {
            AgentInstanceId = instance, OwnedSessionId = session,
            Configuration = new(definition, 1, 1, [], [new(Guid.NewGuid(), instance, Guid.NewGuid(), "work-drive", 1, at, at)]),
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var offered = Assert.Single(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.Contains("check_credential_connection", offered.ParametersJson); Assert.DoesNotContain("update_persona", offered.ParametersJson); Assert.DoesNotContain("update_preferences", offered.ParametersJson);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { Configuration = new(definition, 1, 1, []) }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        var unavailable = definition with { Environment = definition.Environment with { ToolAllowlist = [] } };
        Assert.DoesNotContain(ToolCatalog.For(unavailable, context with { Definition = unavailable }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        var uncertain = new TenantApprovalOutcome(context.ExecutionContext.TenantId, context.ExecutionContext.RunId, Guid.NewGuid(), Guid.NewGuid(), instance,
            activation, TenantApprovalStatus.Approved, TenantOperationOutcome.Indeterminate, "credential_check_uncertain", 1, 1);
        Assert.Empty(ToolCatalog.For(definition, context with { ApprovalOutcome = uncertain }, ToolConfigurationGates.AllowAll));
    }

    private static readonly AgentIdentity Persona = new("Persona", "Assistant", "Description", "Clear");
    private static string Arguments(AgentIdentity? persona = null) => JsonSerializer.Serialize(new { operation = "update_persona", persona = persona ?? Persona }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    [Fact]
    public void Exact_persona_contract_accepts_only_its_typed_operation_and_rejects_caller_authority()
    {
        Assert.Equal(Persona, TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments())).Persona);
        foreach (var key in new[] { "tenantId", "runId", "principalId", "permission", "grantId", "resourceId", "expectedRevision" })
        {
            var input = JsonNode.Parse(Arguments())!.AsObject(); input[key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, input.ToJsonString())));
        }
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments().Replace("update_persona", "assign_role"))));
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments()[..^1] + ",\"operation\":\"update_persona\"}")));
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments(Persona with { Name = new('x', 81) }))));
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments(Persona with { Description = new('x', 1025) }))));
    }

    [Fact]
    public void Preference_authorization_accepts_only_its_exact_typed_payload_and_rejects_mixed_or_selected_authority()
    {
        const string arguments = """{"operation":"update_preferences","preferences":{"set":{"language":"vi"},"clear":[]}}""";
        var intent = TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, arguments));
        Assert.Null(intent.Persona); Assert.Equal("vi", intent.Preferences!.Language);
        foreach (var key in new[] { "tenantId", "runId", "permission", "expectedRevision", "persona" })
        {
            var json = JsonNode.Parse(arguments)!.AsObject(); json[key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, json.ToJsonString())));
        }
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, arguments.Replace("language", "maxOutputTokens", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Offering_requires_tenant_context_and_pinned_Settings_eligibility_and_never_becomes_a_definition_allowlist_permission()
    {
        var definition = await CapabilityProjectionTests.Definition();
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with
        {
            AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, AgentCore.Domain.Conversation.ExecutionOrigin.UserMessage)
        };
        Assert.DoesNotContain(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        definition = definition with { SelfManagement = new(HarnessManagementMode.Assisted, [HarnessManagementScope.Settings], [], []) };
        Assert.Contains(ToolCatalog.For(definition, context with { Definition = definition }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { Definition = definition, ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.False(TenantApprovalToolContract.Descriptor().DefinitionAuthorizable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Direct_preferences_are_context_only_and_require_explicit_Managed_auto_apply_in_both_projection_modes(bool capabilities)
    {
        var definition = await CapabilityProjectionTests.Definition();
        definition = definition with { Environment = definition.Environment! with { Capabilities = capabilities ? definition.Environment!.Capabilities : null },
            SelfManagement = new(HarnessManagementMode.Managed, [HarnessManagementScope.Settings], [], [], ManagedAutoApply: [ManagementAutoApply.ConversationPreferences]) };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with {
            AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, AgentCore.Domain.Conversation.ExecutionOrigin.UserMessage) };
        Assert.Contains(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == TenantPreferenceToolContract.Name);
        foreach (var blocked in new[] {
            definition with { SelfManagement = definition.SelfManagement with { Mode = HarnessManagementMode.Assisted } },
            definition with { SelfManagement = definition.SelfManagement with { ManagedAutoApply = [] } },
            definition with { SelfManagement = definition.SelfManagement with { Frozen = true } } })
            Assert.DoesNotContain(ToolCatalog.For(blocked, context with { Definition = blocked }, ToolConfigurationGates.AllowAll), t => t.Name == TenantPreferenceToolContract.Name);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == TenantPreferenceToolContract.Name);
        Assert.False(TenantPreferenceToolContract.Descriptor().DefinitionAuthorizable);
        Assert.False(TenantPreferenceToolContract.Descriptor().Discoverable);
    }
}
