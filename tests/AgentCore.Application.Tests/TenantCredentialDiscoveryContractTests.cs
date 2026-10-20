using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Tests;

public sealed class TenantCredentialDiscoveryContractTests
{
    [Theory] [InlineData("email.accounts", "email")] [InlineData("drive.accounts", "drive")]
    public void Family_is_derived_from_the_offered_tool_and_round_trips_in_exact_review(string tool, string family)
    {
        var args = TenantCredentialDiscoveryContract.Parse("{\"limit\":1}", tool);
        Assert.Equal(family, args.Family); Assert.Equal(args, TenantCredentialDiscoveryContract.Parse(args.Json));
        Assert.Equal(args.Json, TenantApprovalRules.CredentialReadArguments(args.Json));
        var intent = TenantApprovalToolContract.Parse(new("discovery", ToolCatalog.RequestAuthorization,
            JsonSerializer.Serialize(new { operation = TenantCredentialReadAuthorizationContract.Operation, arguments = JsonSerializer.Deserialize<JsonElement>(args.Json) })));
        Assert.Equal(args.Json, intent.MetadataArguments);
        Assert.Throws<ArgumentException>(() => TenantCredentialDiscoveryContract.Parse(args.Json, tool));
        var id = Guid.NewGuid(); var session = Guid.NewGuid(); var instance = Guid.NewGuid();
        var refs = JsonSerializer.Serialize(new[] { new CredentialReadReference(id, Guid.NewGuid(), "work", 1, 1, "Account", "Token") });
        var operation = TenantApprovalRules.PrepareCredentialRead(Guid.NewGuid(), instance, new(session, id, args.Json, refs), 1, 1);
        Assert.Equal(operation, TenantApprovalRules.Rebuild(operation));
        Assert.Contains(TenantCredentialReadOperationRules.Targets(Guid.NewGuid(), operation.CredentialRead!), p => p.Permission == family + ".read");
        Assert.DoesNotContain(TenantCredentialReadOperationRules.Targets(Guid.NewGuid(), operation.CredentialRead!), p => p.Permission == "credentials.use");
    }
    [Theory]
    [InlineData("{\"family\":null}")] [InlineData("{\"family\":\"password\"}")] [InlineData("{\"family\":\"email\",\"family\":\"drive\"}")]
    [InlineData("{\"family\":\"email\",\"tenantId\":\"forged\"}")] [InlineData("{\"family\":\"drive\",\"limit\":101}")]
    public void Discovery_refuses_unknown_or_ambiguous_scope(string json) => Assert.ThrowsAny<Exception>(() => TenantCredentialDiscoveryContract.Parse(json));
    [Theory] [InlineData("email.accounts")] [InlineData("drive.accounts")]
    public async Task Family_offering_requires_binding_selected_capability_and_supported_human_task(string tool)
    {
        var definition = await CapabilityProjectionTests.Definition(tool, ToolCatalog.BackgroundStart);
        var session = Guid.NewGuid(); var activation = Guid.NewGuid(); var instance = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        var execution = new TrustedExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage);
        var context = CapabilityProjectionTests.Context(definition) with { ModelSupportsTools = true, AgentInstanceId = instance, OwnedSessionId = session,
            Configuration = new(definition, 1, 1, [], [new(Guid.NewGuid(), instance, Guid.NewGuid(), "work", 1, at, at)]), ExecutionContext = execution };
        var offered = Assert.Single(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == tool);
        var schema = Assert.Single(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson;
        Assert.Contains("\"" + TenantCredentialDiscoveryContract.Family(tool) + "\"", schema);
        Assert.DoesNotContain(tool == ToolCatalog.EmailAccounts ? "\"drive\"" : "\"email\"", schema);
        Assert.Contains("cursor", offered.ParametersJson); Assert.DoesNotContain("credentialId", offered.ParametersJson);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { Configuration = new(definition, 1, 1, []) }, ToolConfigurationGates.AllowAll), t => t.Name == tool);
        var autonomous = context with { ExecutionContext = new(execution.TenantId, execution.AgentPrincipalId, null, session, execution.RunId, activation, activation, null, null, ExecutionOrigin.ScheduledOccurrence) };
        Assert.DoesNotContain(ToolCatalog.For(definition, autonomous, ToolConfigurationGates.AllowAll), t => t.Name == tool);
    }
}
