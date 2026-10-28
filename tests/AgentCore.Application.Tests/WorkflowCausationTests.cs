using System.Text.Json;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class WorkflowCausationTests
{
    [Theory] [InlineData(ActivationKind.UserTurn)] [InlineData(ActivationKind.Initiative)] [InlineData(ActivationKind.ManualBackground)]
    public void Unqualified_evidence_cannot_select_a_root_or_Automation_chain(ActivationKind kind)
    {
        var fake = Guid.NewGuid(); var root = Root(kind, false, fake);
        var causation = WorkflowCausationRules.FromRoot(root);
        Assert.Equal(root.AgentRunId, causation.RootAgentRunId); Assert.Equal(0, causation.TriggerDepth); Assert.Empty(causation.VisitedAutomationIds);
    }
    [Theory] [InlineData(ActivationKind.CoreEvent)] [InlineData(ActivationKind.ApplicationEvent)] [InlineData(ActivationKind.ScheduledWork)] [InlineData(ActivationKind.ManualBackground)]
    public void Qualified_occurrence_keeps_its_frozen_original_root_and_chain(ActivationKind kind)
    {
        var actual = Guid.NewGuid(); var root = Root(kind, true, actual);
        var causation = WorkflowCausationRules.FromRoot(root);
        Assert.Equal(actual, causation.RootAgentRunId); Assert.Equal(1, causation.TriggerDepth); Assert.Equal(actual, Assert.Single(causation.VisitedAutomationIds));
    }
    private static AgentRun Root(ActivationKind kind, bool occurrence, Guid source)
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = JsonSerializer.Serialize(new { triggerContext = new { causation = new { rootAgentRunId = source, triggerDepth = 1, visitedAutomationIds = new[] { source } } } });
        var activation = Activation.Start(Guid.NewGuid(), Guid.NewGuid(), kind, kind == ActivationKind.UserTurn ? [Guid.NewGuid()] : [], null,
            occurrence ? Guid.NewGuid() : null, null, null, "workflow-causation-test", now, evidence);
        return AgentRun.Create(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), new(activation, "causation", 1,
            new AgentIdentity("Source", "Role", "Description", "Tone"), Guid.NewGuid(), AgentRunOutputContract.ConversationResponse),
            new("synthetic", "synthetic", "synthetic", null), 3, now);
    }
}
