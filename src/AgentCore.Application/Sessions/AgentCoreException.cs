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
}

public static class AgentCoreErrors
{
    public static AgentCoreException Validation(string detail) =>
        new("ValidationError", detail, 400);

    public static AgentCoreException NotFound(string detail) =>
        new("NotFound", detail, 404);

    public static AgentCoreException VoiceUnavailable() =>
        new("VoiceUnavailable", "Voice is not available for this agent.", 409);

    public static AgentCoreException Persistence(string detail) =>
        new("SessionPersistenceUnavailable", detail, 503, fatal: false);

    public static AgentCoreException Conflict(string detail) =>
        new("Conflict", detail, 409, fatal: true);
}
