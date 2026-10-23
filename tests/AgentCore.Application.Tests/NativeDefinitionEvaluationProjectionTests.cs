using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class NativeDefinitionEvaluationProjectionTests
{
    [Fact]
    public async Task New_capability_evaluation_checks_projection_and_keeps_execution_authority_separate()
    {
        var candidate = PublishableExaminerCandidate() with { Environment = new(Capabilities: new("Selected", [ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceWrite]), Projection: new([])) };
        var runner = AdminEvaluationTestSupport.CreateRunner();
        var scenario = new DefinitionEvaluationScenario(Guid.NewGuid(), "capability-offered", 1, "Capability projection", "Check the projected capability",
            DefinitionEvaluationRequirementLevel.Required, DefinitionEvaluationCheckType.ToolOffered, ToolCatalog.CapabilitiesLoad, DateTimeOffset.UtcNow);
        Assert.True((await runner.RunAsync(candidate, [], scenario)).Passed);
        Assert.True((await runner.RunAsync(candidate, [], scenario with { CheckType = DefinitionEvaluationCheckType.ToolNotOffered, ToolName = ToolCatalog.WorkspaceWrite })).Passed);
        Assert.False((await runner.RunAsync(candidate, [], scenario with { CheckType = DefinitionEvaluationCheckType.ExternalActionDenied, ToolName = ToolCatalog.WorkspaceWrite })).Passed);
    }

    private static AgentDefinitionCandidate PublishableExaminerCandidate() =>
        AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Examiner) with
        {
            Environment = RoleEnvironment.Empty with
            {
                ToolAllowlist = [ToolCatalog.KnowledgeRetrieve]
            }
        };

}
