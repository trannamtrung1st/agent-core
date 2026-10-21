using AgentCore.Contracts.Realtime;

namespace AgentCore.Api.Realtime;

internal static class SessionCommandValidation
{
    internal static CommandAck Validate(IRealtimeCommand? command, bool attach, string expectedType)
    {
        if (command is null) return Reject("", "Protocol", "ProtocolError", "A command is required.", true, null);
        if (command.CorrelationId is not null || command.CausationId is not null)
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "Client must not set correlationId or causationId.", true, null);
        }

        if (command.ProtocolVersion != 1)
        {
            var mismatch = Reject(command.EventId, "Protocol", "ProtocolVersionMismatch", "Unsupported protocol version.", true, null);
            mismatch.Error!.Extensions = new Dictionary<string, object?> { ["supportedVersions"] = new[] { 1 } };
            return mismatch;
        }

        if (!string.Equals(command.Type, expectedType, StringComparison.Ordinal))
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "Command type does not match the hub method.", true, null);
        }

        if (!Guid.TryParse(command.SessionId, out _) || !Guid.TryParse(command.EventId, out _))
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "IDs must be UUIDs.", true, null);
        }

        if (attach && command.Sequence != 0)
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "Attach sequence must be 0.", true, null);
        }

        if (!attach && command.Sequence < 1)
        {
            return Reject(command.EventId, "Protocol", "StaleCommand", "Control sequence must increase from 1.", false, null);
        }

        return Accept(command.EventId);
    }

    private static CommandAck Accept(string eventId) => new() { EventId = eventId, Accepted = true };
    private static CommandAck Reject(string id, string category, string code, string message, bool fatal, int? retry) => new()
    {
        EventId = id, Accepted = false,
        Error = new() { Category = category, Code = code, Message = message, Fatal = fatal, RetryAfterMs = retry }
    };
}
