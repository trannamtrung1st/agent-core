using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;

namespace AgentCore.Application.Tests;

public sealed class TriggerCommandAuthorizerTests
{
    [Theory]
    [InlineData(TriggerKind.UserTurn, TriggerAuthorizationClassification.CurrentUserTurn)]
    [InlineData(TriggerKind.ScheduledOccurrence, TriggerAuthorizationClassification.Occurrence)]
    [InlineData(TriggerKind.ApplicationEvent, TriggerAuthorizationClassification.Occurrence)]
    [InlineData(TriggerKind.CoreEvent, TriggerAuthorizationClassification.Occurrence)]
    [InlineData(TriggerKind.EnvironmentUpdate, TriggerAuthorizationClassification.Environment)]
    [InlineData(TriggerKind.LongSilence, TriggerAuthorizationClassification.Initiative)]
    [InlineData(TriggerKind.BackgroundCompleted, TriggerAuthorizationClassification.Missing)]
    public void Provenance_uses_runtime_trigger_kind_only(TriggerKind kind, TriggerAuthorizationClassification expected) =>
        Assert.Equal(expected, TriggerAuthorization.Classify(kind));
}
