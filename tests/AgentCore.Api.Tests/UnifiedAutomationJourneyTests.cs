using AgentCore.Application.Execution;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class UnifiedAutomationJourneyTests
{

    [Fact]
    public void Correlated_events_are_bounded_and_preserve_the_root()
    {
        var root = Guid.NewGuid();
        string Payload(int depth) => $$$"""{"eventId":"new","rootAgentRunId":"{{{root}}}","triggerDepth":{{{depth}}},"data":{"orderReference":"1001"}}""";
        Assert.True(ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(Payload(4)), out var evidence, out _, out _, out _));
        Assert.Contains(root.ToString(), evidence);
        Assert.False(ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(Payload(5)), out _, out _, out _, out var reason));
        Assert.Equal("trigger_depth_exceeded", reason);
    }

    internal static async Task Drain(IServiceProvider s)
    {
        await s.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        await s.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync();
        await s.ExecuteRunsAsync(100);
    }
}
