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
    public const int MaxRepairDisplayCharacters = 64 * 1024;

    public const string MissingDisplayTextInstruction =
        """
        The previous terminal answer was rejected because its visible reply was empty.
        Using the existing conversation and tool results, return only the final user-visible answer text. Do not emit JSON, call tools, repeat completed work, or request another action.
        """;

    public const string InvalidBlocksInstruction =
        """
        The previous terminal answer was rejected because its rich block metadata was invalid.
        Using the existing conversation and tool results, return only the final user-visible answer text. Do not emit JSON, rich blocks, call tools, repeat completed work, or request another action.
        """;

    public static ProtocolFailureDisposition Disposition(string? reason) => reason switch
    {
        ProviderFailureReason.SpeechOmitted or ProviderFailureReason.SpeechMalformed => ProtocolFailureDisposition.Normalize,
        ProviderFailureReason.MissingDisplayText or ProviderFailureReason.InvalidBlocks => ProtocolFailureDisposition.Repairable,
        _ => ProtocolFailureDisposition.Terminal
    };

    public static string? RepairInstruction(string? reason) => reason switch
    {
        ProviderFailureReason.MissingDisplayText => MissingDisplayTextInstruction,
        ProviderFailureReason.InvalidBlocks => InvalidBlocksInstruction,
        _ => null
    };
}
