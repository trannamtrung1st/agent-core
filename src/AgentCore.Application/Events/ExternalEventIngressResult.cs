namespace AgentCore.Application.Events;

public enum ExternalEventIngressKind
{
    Admitted,
    Duplicate,
    Unauthorized,
    Invalid
}

public sealed record ExternalEventIngressResult(
    ExternalEventIngressKind Kind,
    Guid? EventId,
    string? Code);
