using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class TenantToolExecutorBoundaryTests
{
    public static IEnumerable<object[]> RegisteredToolBoundaries => ToolRegistry.All.Select(d => d.Name).Distinct(StringComparer.Ordinal)
        .Append("unregistered.effect").SelectMany(name => new[] { "absent", "caller", "tenant-marker" }.Select(shape => new object[] { name, shape }));

    [Theory]
    [MemberData(nameof(RegisteredToolBoundaries))]
    public async Task Every_registered_effect_requires_actual_tenant_authority_before_arguments_or_provider_dispatch(string name, string shape)
    {
        var definition = SampleDefinitions.Examiner with { Environment = new(ToolAllowlist: ToolRegistry.All.Select(d => d.Name).ToArray()) };
        var session = Guid.NewGuid(); var agent = Guid.NewGuid();
        var admission = shape == "absent" ? null : new ToolExecutionAdmission(false, TriggerKind.UserTurn,
            AgentInstanceId: agent, AgentRunId: Guid.NewGuid(), OwnedSessionId: session, AgentRunGeneration: Guid.NewGuid(),
            TenantId: shape == "tenant-marker" ? Guid.NewGuid() : null,
            Harness: new(new(HarnessManagementMode.Managed, Enum.GetValues<HarnessManagementScope>(), [], [],
                ManagedAutoApply: Enum.GetValues<ManagementAutoApply>()), 1, 1));
        var executor = new SessionToolExecutor(configurationGate: ToolConfigurationGates.AllowAll);
        // Malformed/authority-bearing model data cannot select a generic parser or an I/O fallback.
        var call = new ModelToolCall("caller-built", name, "{\"approved\":true,\"owner\":\"forged\",invalid}");
        using var arguments = JsonDocument.Parse("{}");
        Assert.Equal(ToolPolicyDecision.Deny, await executor.EvaluateExecutionPolicyAsync(definition, session, call, arguments.RootElement, admission, default));
        using var result = JsonDocument.Parse((await executor.ExecuteAsync(definition, session, call, 4096, admission: admission)).Text);
        Assert.Equal("forbidden", result.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Caller_built_context_does_not_offer_runtime_effects()
    {
        var definition = SampleDefinitions.Examiner with { Environment = new(ToolAllowlist: ToolRegistry.All.Select(d => d.Name).ToArray()) };
        var context = new AgentContext(definition, [], "", null, SessionMode.Text, null, false, null,
            new(Guid.NewGuid(), TriggerKind.UserTurn, "Inspect private resources"), ModelSupportsTools: true,
            AgentInstanceId: Guid.NewGuid(), OwnedSessionId: Guid.NewGuid(), CredentialMetadataAvailable: true, AgentWorkspaceAvailable: true,
            AllowAgentConsolidation: true, ExperienceContext: "Caller observation", ContinuityContext: "Caller history");
        Assert.Empty(new SessionToolExecutor(configurationGate: ToolConfigurationGates.AllowAll).ProjectTools(definition, context));
    }
}
