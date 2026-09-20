namespace AgentCore.Application.Sessions;

public sealed class AgentCoreException : Exception
{
    public AgentCoreException(string code, string message, int statusCode, bool fatal = false)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Fatal = fatal;
    }

    public string Code { get; }
    public int StatusCode { get; }
    public bool Fatal { get; }
    public int? RetryAfterMs { get; init; }
}

public static class AgentCoreErrors
{
    public static AgentCoreException Validation(string detail) =>
        new("ValidationError", detail, 400);

    public static AgentCoreException NotFound(string detail) =>
        new("NotFound", detail, 404);

    public static AgentCoreException VoiceUnavailable() =>
        new("VoiceUnavailable", "Voice is not available for this agent.", 409);

    public static AgentCoreException SessionInUse() =>
        new("SessionInUse", "Session is attached to another connection.", 409);

    public static AgentCoreException SessionBusy() =>
        new("SessionBusy", "Session is busy generating a response.", 409);

    public static AgentCoreException SessionCapacityExceeded() =>
        new("SessionCapacityExceeded", "Maximum active sessions reached.", 429) { RetryAfterMs = 5000 };

    public static AgentCoreException Protocol(string detail) =>
        new("ProtocolError", detail, 400);

    public static AgentCoreException ProtocolVersionMismatch() =>
        new("ProtocolVersionMismatch", "Unsupported protocol version.", 400);

    public static AgentCoreException StaleCommand() =>
        new("StaleCommand", "Command is stale for this attachment.", 409);

    public static AgentCoreException Persistence(string detail) =>
        new("SessionPersistenceUnavailable", detail, 503, fatal: false);

    public static AgentCoreException Conflict(string detail) =>
        new("Conflict", detail, 409, fatal: true);

    public static AgentCoreException ShuttingDown() =>
        new("ServiceUnavailable", "The host is shutting down.", 503) { RetryAfterMs = 1000 };

    public static AgentCoreException Unauthorized() =>
        new("Unauthorized", "Owner capability is missing or invalid.", 401);

    public static AgentCoreException Forbidden(string detail) =>
        new("Forbidden", detail, 403);

    public static AgentCoreException SessionArchived() =>
        new("SessionArchived", "Archived sessions cannot use attachments until unarchive.", 409);

    public static AgentCoreException ModelCapabilityUnsupported() =>
        new(
            "ModelCapabilityUnsupported",
            "This model cannot read images. Choose a vision-capable model to send this attachment.",
            409,
            fatal: false);

    public static AgentCoreException WorkspaceQuotaExceeded() =>
        new("WorkspaceQuotaExceeded", "Workspace writes cannot exceed 250 MiB.", 413);

    public static AgentCoreException ArtifactQuotaExceeded() =>
        new("ArtifactQuotaExceeded", "Artifact writes cannot exceed 50 MiB each or 250 MiB per session.", 413);
}
