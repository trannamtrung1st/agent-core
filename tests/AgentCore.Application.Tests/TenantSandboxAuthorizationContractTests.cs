using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class TenantSandboxAuthorizationContractTests
{
    private const string Arguments = """{"operation":"run_sandbox_command","profileId":"p-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","command":{"argv":["printf","%s","quoted ; $()"],"cwd":"/working","timeoutSeconds":30,"background":false}}""";
    [Fact]
    public void Exact_command_intent_matches_only_saved_parameters_and_rejects_selected_authority_and_ambiguous_values()
    {
        var intent = TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments));
        var command = intent.Sandbox!;
        Assert.Equal("quoted ; $()", command.Command.Argv![2]);
        var operation = TenantApprovalRules.PrepareSandbox(Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), command.ProfileId, 1,
            TenantApprovalRules.CanonicalSandboxCommand(command.Command), 0, null, null, null, null, HomeAccess.ReadWrite), 1, 1);
        Assert.True(intent.Matches(operation));
        Assert.False((intent with { Sandbox = command with { Command = command.Command with { TimeoutSeconds = 31 } } }).Matches(operation));
        Assert.False((intent with { Sandbox = command with { ProfileId = "another-profile" } }).Matches(operation));
        Assert.False((intent with { CredentialReference = "work-drive" }).Matches(operation));
        foreach (var key in new[] { "tenantId", "sessionId", "runId", "permission", "expectedRevision", "grantId", "sandboxCommand", "persona" })
        {
            var json = JsonNode.Parse(Arguments)!.AsObject(); json[key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, json.ToJsonString())));
        }
        foreach (var key in new[] { "profileId", "environment", "uid", "host", "registryCredentialId", "expectedGeneration" })
        {
            var json = JsonNode.Parse(Arguments)!; json["command"]![key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, json.ToJsonString())));
        }
        foreach (var patch in new Action<JsonNode>[] {
            j => j["command"]!["timeoutSeconds"] = null, j => j["command"]!["timeoutSeconds"] = "30", j => j["command"]!["timeoutSeconds"] = 1.5,
            j => j["command"]!["timeoutSeconds"] = 0, j => j["command"]!["timeoutSeconds"] = 181, j => j["command"]!["background"] = "false",
            j => j["command"]!["command"] = "echo mixed", j => j["command"]!["argv"] = null, j => j["command"]!["cwd"] = "/working/../home",
            j => j["command"]!.AsObject().Remove("cwd"), j => j["command"]!.AsObject().Remove("background") })
        {
            var json = JsonNode.Parse(Arguments)!; patch(json);
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, json.ToJsonString())));
        }
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments.Replace("\"background\":false", "\"background\":false,\"background\":true"))));
    }

    [Fact]
    public async Task Sandbox_authorization_offers_only_admitted_exact_choices_independently_of_Settings_and_keeps_direct_dispatch_closed()
    {
        var definition = await CapabilityProjectionTests.Definition();
        var profile = new SandboxProfile("p-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, "Owned profile", "Information", true, "sha256:" + new string('a', 64), AdmittedForExecution: true);
        definition = definition with { SelfManagement = HarnessManagementPolicy.Disabled, SandboxPolicy = new(profile.Id, [profile.Id, "unverified"]),
            ExecutionSandboxProfiles = [profile, profile with { Id = "unverified", AdmittedForExecution = false }],
            ExecutionPolicy = definition.ExecutionPolicy! with { Capabilities = new("Selected", [ToolCatalog.SandboxExec]) } };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var offered = ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll);
        var tool = Assert.Single(offered, t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.Contains("run_sandbox_command", tool.ParametersJson); Assert.Contains("p-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", tool.ParametersJson); Assert.DoesNotContain("unverified", tool.ParametersJson);
        Assert.DoesNotContain("update_persona", tool.ParametersJson); Assert.DoesNotContain("update_preferences", tool.ParametersJson); Assert.DoesNotContain("check_credential_connection", tool.ParametersJson);
        Assert.DoesNotContain(offered, t => t.Name == ToolCatalog.SandboxExec);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        foreach (var blocked in new[] { definition with { ExecutionSandboxProfiles = [] }, definition with { ExecutionSandboxProfiles = [profile with { AdmittedForExecution = false }] },
            definition with { ExecutionPolicy = definition.ExecutionPolicy with { Capabilities = new("Selected", []) } } })
        {
            var blockedTools = ToolCatalog.For(blocked, context with { Definition = blocked }, ToolConfigurationGates.AllowAll);
            Assert.DoesNotContain(blockedTools, t => t.Name == ToolCatalog.SandboxExec);
            var authorization = blockedTools.SingleOrDefault(t => t.Name == ToolCatalog.RequestAuthorization);
            if (authorization is not null) Assert.DoesNotContain("run_sandbox_command", authorization.ParametersJson);
        }
    }
}
