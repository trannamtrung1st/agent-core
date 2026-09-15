using AgentCore.Application.Ports;

namespace AgentCore.Application.Testing;

public static class SyntheticEnvironmentDriver
{
    public static EnvironmentEvent OrderShipped(Guid eventId, string orderReference, string status = "shipped") =>
        new(
            eventId,
            "order_status_changed",
            new Dictionary<string, string>
            {
                ["orderReference"] = orderReference,
                ["status"] = status
            });

    public static EnvironmentEvent Unfinished(Guid eventId, string topic) =>
        new(
            eventId,
            "unfinished_interaction",
            new Dictionary<string, string>
            {
                ["topic"] = topic
            });
}
