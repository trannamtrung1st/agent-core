namespace AgentCore.Domain.Diagnostics;

/// <summary>
/// Closed tokens for provider diagnostic detail. Values name a contract violation or provider failure phase.
/// They are not model output, tool arguments, or prompts.
/// </summary>
public static class DiagnosticDetailAllowlist
{
    public static readonly IReadOnlySet<string> FailureReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "invalidJson",
        "responseTooLarge",
        "missingDisplayText",
        "invalidSpeech",
        "invalidSpeechMode",
        "missingCustomSpeechText",
        "speechOmitted",
        "speechMalformed",
        "invalidBlocks",
        "invalidMemory",
        "invalidMemoryProposal",
        "responseFunctionArgumentsInvalid",
        "invalidMarkerEnvelope",
        "modelSuppliedDestination",
        "unknownAction",
        "unknownDisposition",
        "outputLimit",
        "toolCallTruncated",
        "transportFailure",
        "http5xx",
        "setupTimeout",
        "totalTimeout",
        "streamIdle",
        "streamMalformed",
        "providerStreamError",
        "streamIncomplete",
        "incompleteToolCall",
        "circuitOpen",
        "streamLimit"
    };

    public static readonly IReadOnlySet<string> ProtocolRepairs = new HashSet<string>(StringComparer.Ordinal)
    {
        "attempted"
    };

    public static readonly IReadOnlySet<string> ProtocolRepairOutcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        "succeeded",
        "failed",
        "cancelled"
    };

    public static readonly IReadOnlySet<string> ResponseChannels = new HashSet<string>(StringComparer.Ordinal)
    {
        "responseFunction",
        "structuredOutput",
        "markerCompatibility",
        "toolCall"
    };
}
