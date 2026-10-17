using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Infrastructure.Tests;

public sealed class SyntheticAuthorizationAuditTests
{
    [Fact]
    public async Task Synthetic_audit_fixture_emits_only_the_offered_readonly_inspection_with_a_resource_argument_and_no_caller_authority()
    {
        var resource = Guid.NewGuid(); var model = new ScriptedLanguageModel(["fallback"]);
        var request = new ModelRequest(Guid.NewGuid(), [new(ModelRole.User, $"{ScriptedLanguageModel.AuthorizationAuditMarker} agents.read {resource:D}")],
            Tools: [IamToolContracts.Descriptor(ToolCatalog.IamCheckAccess).ModelDefinition]);
        var events = new List<ModelGenerationEvent>(); await foreach (var e in model.GenerateAsync(request)) events.Add(e);
        var call = Assert.Single(events.OfType<ModelToolCallEvent>()).Call;
        Assert.Equal(ToolCatalog.IamCheckAccess, call.Name); Assert.Contains(resource.ToString("D"), call.ArgumentsJson);
        Assert.DoesNotContain("principal", call.ArgumentsJson); Assert.DoesNotContain("tenant", call.ArgumentsJson);
        var disabled = new List<ModelGenerationEvent>();
        await foreach (var e in model.GenerateAsync(request with { Tools = [] })) disabled.Add(e);
        Assert.Empty(disabled.OfType<ModelToolCallEvent>());
    }
}
