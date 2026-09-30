using AgentCore.Application.Memory;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Execution;

/// <summary>
/// Provider-neutral step before validation. Destination fields are present only so a
/// model-supplied target can fail closed. They are not copied onto an accepted step.
/// </summary>
public sealed record AgentStepCandidate(
    string? Disposition,
    IReadOnlyList<AgentActionCandidate>? Actions,
    IReadOnlyList<MemoryProposal>? MemoryProposals = null);

public sealed record AgentActionCandidate(
    string? Kind,
    string? DisplayText = null,
    ModelSpeechProjection? Speech = null,
    IReadOnlyList<ModelResponseBlock>? Blocks = null,
    string? SessionId = null,
    string? Destination = null,
    string? ProfileId = null,
    string? Tenant = null,
    string? Recipient = null);

public abstract record AgentStepNormalizationResult;

public sealed record AgentStepAccepted(AgentStep Step) : AgentStepNormalizationResult;

public sealed record AgentStepRejected(AgentStepRejection Rejection) : AgentStepNormalizationResult;

public enum AgentStepRejectionCategory
{
    UnknownDisposition,
    UnknownAction,
    MalformedPayload,
    ModelSuppliedDestination
}

public sealed record AgentStepRejection(AgentStepRejectionCategory Category)
{
    public ProviderErrorCode Code => ProviderErrorCode.InvalidResponse;

    public string FailureReason => Category switch
    {
        AgentStepRejectionCategory.UnknownDisposition => "unknownDisposition",
        AgentStepRejectionCategory.UnknownAction => "unknownAction",
        AgentStepRejectionCategory.ModelSuppliedDestination => "modelSuppliedDestination",
        _ => "malformedPayload"
    };
}

public static class AgentStepNormalizer
{
    public const string ChatRespondKind = "chat.respond";

    public static AgentStepCandidate FromSemanticResponse(ModelSemanticResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new AgentStepCandidate(
            string.IsNullOrWhiteSpace(response.Disposition)
                ? nameof(AgentStepDisposition.Complete)
                : response.Disposition,
            [
                new AgentActionCandidate(
                    string.IsNullOrWhiteSpace(response.ActionKind) ? ChatRespondKind : response.ActionKind,
                    response.DisplayText ?? string.Empty,
                    response.Speech ?? new ModelSpeechProjection(ModelSpeechMode.Same, null),
                    response.Blocks ?? [])
            ],
            response.Memory);
    }

    public static AgentStepNormalizationResult Normalize(AgentStepCandidate? candidate)
    {
        if (candidate is null)
        {
            return Reject(AgentStepRejectionCategory.MalformedPayload);
        }

        if (string.IsNullOrWhiteSpace(candidate.Disposition))
        {
            return Reject(AgentStepRejectionCategory.MalformedPayload);
        }

        if (!TryParseDisposition(candidate.Disposition, out var disposition))
        {
            return Reject(AgentStepRejectionCategory.UnknownDisposition);
        }

        if (candidate.Actions is null)
        {
            return Reject(AgentStepRejectionCategory.MalformedPayload);
        }

        ChatRespondAction? chat = null;
        foreach (var action in candidate.Actions)
        {
            if (action is null)
            {
                return Reject(AgentStepRejectionCategory.MalformedPayload);
            }

            if (HasDestination(action))
            {
                return Reject(AgentStepRejectionCategory.ModelSuppliedDestination);
            }

            if (string.IsNullOrWhiteSpace(action.Kind))
            {
                return Reject(AgentStepRejectionCategory.MalformedPayload);
            }

            if (!string.Equals(action.Kind, ChatRespondKind, StringComparison.Ordinal))
            {
                return Reject(AgentStepRejectionCategory.UnknownAction);
            }

            if (chat is not null)
            {
                return Reject(AgentStepRejectionCategory.MalformedPayload);
            }

            if (action.DisplayText is null
                || action.Speech is null
                || action.Blocks is null
                || !Enum.IsDefined(action.Speech.Mode))
            {
                return Reject(AgentStepRejectionCategory.MalformedPayload);
            }

            foreach (var block in action.Blocks)
            {
                if (!Enum.IsDefined(block.Kind))
                {
                    return Reject(AgentStepRejectionCategory.MalformedPayload);
                }
            }

            chat = new ChatRespondAction(
                action.DisplayText,
                action.Speech,
                action.Blocks.ToArray());
        }

        var memory = candidate.MemoryProposals is { Count: > 0 }
            ? candidate.MemoryProposals.ToArray()
            : Array.Empty<MemoryProposal>();
        IReadOnlyList<AgentAction> actions = chat is null ? [] : [chat];
        return new AgentStepAccepted(new AgentStep(disposition, actions, memory));
    }

    private static AgentStepRejected Reject(AgentStepRejectionCategory category) =>
        new(new AgentStepRejection(category));

    private static bool TryParseDisposition(string value, out AgentStepDisposition disposition)
    {
        disposition = default;
        foreach (var name in Enum.GetNames<AgentStepDisposition>())
        {
            if (!string.Equals(name, value, StringComparison.Ordinal))
            {
                continue;
            }

            return Enum.TryParse(value, ignoreCase: false, out disposition);
        }

        return false;
    }

    private static bool HasDestination(AgentActionCandidate action) =>
        action.SessionId is not null
        || action.Destination is not null
        || action.ProfileId is not null
        || action.Tenant is not null
        || action.Recipient is not null;
}
