using System.Text;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Tests;

public sealed class OrderPlacedAdmissionTests
{

    [Fact]
    public void Envelope_preserves_bounded_untrusted_data_and_rejects_ambiguous_shapes()
    {
        var json = ExternalEventEnvelope.Build("evt-1", "1001");
        Assert.True(ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(json), out var evidence, out var eventId, out _, out var error), error);
        Assert.Equal("evt-1", eventId);
        using var document = JsonDocument.Parse(evidence);
        Assert.Equal("1001", document.RootElement.GetProperty("data").GetProperty("orderReference").GetString());
        Assert.False(ExternalEventEnvelope.TryNormalize("{}"u8, out _, out _, out _, out _));
        Assert.False(ExternalEventEnvelope.TryNormalize("{"u8, out _, out _, out _, out _));
        Assert.False(ExternalEventEnvelope.TryNormalize("""{"eventId":"evt-1","data":{},"instructions":"do this"}"""u8, out _, out _, out _, out _));
        Assert.False(ExternalEventEnvelope.TryNormalize("""{"eventId":"evt-1","data":{"x":1,"x":2}}"""u8, out _, out _, out _, out _));
        Assert.True(ExternalEventEnvelope.TryNormalize("""{"eventId":"invoice-1","data":{"invoice":{"amount":19,"currency":"USD"},"instructions":"untrusted"}}"""u8, out _, out _, out _, out _));
    }

}
