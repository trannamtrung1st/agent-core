namespace AgentCore.Application.Execution;

public enum ChatAdmissionOutcome
{
    Allow,
    AlreadyAccepted,
    DenyDetached,
    DenyStale,
    DenyDestination,
    DenyUnknownKind
}

/// <summary>
/// Explicit Chat policy for one trusted session. Ordinary same-session Chat is allowed
/// and does not require approval. The bound session is the controller session.
/// </summary>
public sealed record ChatAdmissionDecision(
    ChatAdmissionOutcome Outcome,
    Guid BoundSessionId,
    bool RequiresApproval);

public sealed record ChatAdmissionRequest(
    string? ActionKind,
    Guid TrustedSessionId,
    string? ModelSessionId,
    string? ModelDestination,
    string? ModelProfileId,
    string? ModelTenant,
    string? ModelRecipient,
    bool DetachedExecution,
    bool EpochMatches,
    bool ResponseMatches,
    bool AlreadyAccepted);

public static class ChatActionAdmission
{
    public static ChatAdmissionDecision Evaluate(ChatAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AlreadyAccepted && request.EpochMatches && request.ResponseMatches)
        {
            return new ChatAdmissionDecision(ChatAdmissionOutcome.AlreadyAccepted, request.TrustedSessionId, false);
        }

        if (!request.EpochMatches || !request.ResponseMatches)
        {
            return new ChatAdmissionDecision(ChatAdmissionOutcome.DenyStale, request.TrustedSessionId, false);
        }

        if (request.ModelSessionId is not null
            || request.ModelDestination is not null
            || request.ModelProfileId is not null
            || request.ModelTenant is not null
            || request.ModelRecipient is not null)
        {
            return new ChatAdmissionDecision(ChatAdmissionOutcome.DenyDestination, request.TrustedSessionId, false);
        }

        if (!string.Equals(request.ActionKind, AgentStepNormalizer.ChatRespondKind, StringComparison.Ordinal))
        {
            return new ChatAdmissionDecision(ChatAdmissionOutcome.DenyUnknownKind, request.TrustedSessionId, false);
        }

        if (request.DetachedExecution)
        {
            return new ChatAdmissionDecision(ChatAdmissionOutcome.DenyDetached, request.TrustedSessionId, false);
        }

        return new ChatAdmissionDecision(ChatAdmissionOutcome.Allow, request.TrustedSessionId, RequiresApproval: false);
    }
}
