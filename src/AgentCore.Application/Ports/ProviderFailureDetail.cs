namespace AgentCore.Application.Ports;

/// <summary>
/// Bounded server-side subcodes for provider failures. Safe for structured logs; never carries user content.
/// </summary>
public static class ProviderFailureReason
{
    public const string InvalidJson = "invalidJson";
    public const string ResponseTooLarge = "responseTooLarge";
    public const string MissingDisplayText = "missingDisplayText";
    public const string InvalidSpeech = "invalidSpeech";
    public const string InvalidBlocks = "invalidBlocks";
    public const string InvalidMemory = "invalidMemory";
    public const string InvalidMemoryProposal = "invalidMemoryProposal";
    public const string ResponseFunctionArgumentsInvalid = "responseFunctionArgumentsInvalid";
    public const string InvalidMarkerEnvelope = "invalidMarkerEnvelope";
    public const string ModelSuppliedDestination = "modelSuppliedDestination";
    public const string UnknownAction = "unknownAction";
    public const string UnknownDisposition = "unknownDisposition";
    public const string OutputLimit = "outputLimit";
    public const string ToolCallTruncated = "toolCallTruncated";
}

public static class ProviderResponseChannel
{
    public const string ResponseFunction = "responseFunction";
    public const string StructuredOutput = "structuredOutput";
    public const string MarkerCompatibility = "markerCompatibility";
    public const string ToolCall = "toolCall";
}
