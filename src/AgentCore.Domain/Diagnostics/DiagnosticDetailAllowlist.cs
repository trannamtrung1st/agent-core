namespace AgentCore.Domain.Diagnostics;

/// <summary>
/// Closed tokens for provider diagnostic detail. Values name a contract violation.
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
        "invalidBlocks",
        "invalidMemory",
        "invalidMemoryProposal",
        "responseFunctionArgumentsInvalid",
        "invalidMarkerEnvelope",
        "modelSuppliedDestination",
        "unknownAction",
        "unknownDisposition",
        "outputLimit",
        "toolCallTruncated"
    };

    public static readonly IReadOnlySet<string> ResponseChannels = new HashSet<string>(StringComparer.Ordinal)
    {
        "responseFunction",
        "structuredOutput",
        "markerCompatibility",
        "toolCall"
    };
}
