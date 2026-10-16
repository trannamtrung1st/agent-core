namespace AgentCore.Application.Ports;

/// <summary>
/// How Agent Core treats a semantic-contract defect. Normalize is already applied by the parser.
/// Repairable reasons may receive one correction without substantive tools. Everything else fails closed.
/// </summary>
public enum ProtocolFailureDisposition
{
    Normalize,
    Repairable,
    Terminal
}

public enum ProtocolRepairKind { MissingDisplayText, InvalidBlocks, InvalidJson, InvalidFunctionArguments, InvalidEnvelope, OutputLimit }

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

    public const string FormatInstruction =
        "The previous response format was incomplete or invalid. Using only existing conversation and durable tool evidence, return a concise truthful final answer as plain text. Do not emit JSON, call tools, repeat work, invent findings or claim unverified effects. Explain any incomplete work.";

    public static ProtocolFailureDisposition Disposition(string? reason) => reason switch
    {
        ProviderFailureReason.SpeechOmitted or ProviderFailureReason.SpeechMalformed => ProtocolFailureDisposition.Normalize,
        ProviderFailureReason.MissingDisplayText or ProviderFailureReason.InvalidBlocks
            or ProviderFailureReason.InvalidJson or ProviderFailureReason.ResponseFunctionArgumentsInvalid
            or ProviderFailureReason.InvalidMarkerEnvelope or ProviderFailureReason.OutputLimit => ProtocolFailureDisposition.Repairable,
        _ => ProtocolFailureDisposition.Terminal
    };

    public static ProtocolRepairKind? RepairKind(string? reason) => reason switch
    {
        null => null,
        ProviderFailureReason.MissingDisplayText => ProtocolRepairKind.MissingDisplayText,
        ProviderFailureReason.InvalidBlocks => ProtocolRepairKind.InvalidBlocks,
        ProviderFailureReason.InvalidJson => ProtocolRepairKind.InvalidJson,
        ProviderFailureReason.ResponseFunctionArgumentsInvalid => ProtocolRepairKind.InvalidFunctionArguments,
        ProviderFailureReason.InvalidMarkerEnvelope => ProtocolRepairKind.InvalidEnvelope,
        ProviderFailureReason.OutputLimit => ProtocolRepairKind.OutputLimit,
        _ => throw new ArgumentException("Unsupported protocol repair reason.")
    };
    public static string? RepairReason(ProtocolRepairKind? kind) => kind switch
    {
        null => null,
        ProtocolRepairKind.MissingDisplayText => ProviderFailureReason.MissingDisplayText,
        ProtocolRepairKind.InvalidBlocks => ProviderFailureReason.InvalidBlocks,
        ProtocolRepairKind.InvalidJson => ProviderFailureReason.InvalidJson,
        ProtocolRepairKind.InvalidFunctionArguments => ProviderFailureReason.ResponseFunctionArgumentsInvalid,
        ProtocolRepairKind.InvalidEnvelope => ProviderFailureReason.InvalidMarkerEnvelope,
        ProtocolRepairKind.OutputLimit => ProviderFailureReason.OutputLimit,
        _ => throw new ArgumentException("Unsupported protocol repair kind.")
    };

    public static string? RepairInstruction(string? reason) => reason switch
    {
        ProviderFailureReason.MissingDisplayText => MissingDisplayTextInstruction,
        ProviderFailureReason.InvalidBlocks => InvalidBlocksInstruction,
        ProviderFailureReason.InvalidJson or ProviderFailureReason.ResponseFunctionArgumentsInvalid
            or ProviderFailureReason.InvalidMarkerEnvelope or ProviderFailureReason.OutputLimit => FormatInstruction,
        _ => null
    };
}
