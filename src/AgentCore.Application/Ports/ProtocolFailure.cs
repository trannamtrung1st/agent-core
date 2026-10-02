namespace AgentCore.Application.Ports;

/// <summary>
/// How Agent Core treats a semantic-contract defect. Normalize is already applied by the parser.
/// Repairable reasons may receive one tool-less correction. Everything else fails closed.
/// </summary>
public enum ProtocolFailureDisposition
{
    Normalize,
    Repairable,
    Terminal
}

public static class ProtocolFailures
{
    public const string MissingDisplayTextInstruction =
        """
        The previous terminal response was rejected by the application protocol.

        Reason: chat.respond requires non-empty displayText.

        Produce the terminal response again using the work and tool results already present in this request. Do not repeat completed work. Do not request or call additional tools.
        """;

    public static ProtocolFailureDisposition Disposition(string? reason) => reason switch
    {
        ProviderFailureReason.SpeechOmitted or ProviderFailureReason.SpeechMalformed => ProtocolFailureDisposition.Normalize,
        ProviderFailureReason.MissingDisplayText => ProtocolFailureDisposition.Repairable,
        _ => ProtocolFailureDisposition.Terminal
    };

    public static string? RepairInstruction(string? reason) =>
        Disposition(reason) == ProtocolFailureDisposition.Repairable && reason == ProviderFailureReason.MissingDisplayText
            ? MissingDisplayTextInstruction
            : null;
}
