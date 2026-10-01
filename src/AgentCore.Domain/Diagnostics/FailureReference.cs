namespace AgentCore.Domain.Diagnostics;

public sealed record FailureReference
{
    public const int MaxTokenLength = 64;

    public FailureReference(
        Guid diagnosticId,
        string category,
        string code,
        Guid? correlationId = null,
        string? failureReason = null,
        string? providerResponseChannel = null)
    {
        if (diagnosticId == Guid.Empty)
        {
            throw new ArgumentException("Diagnostic identifier is required.", nameof(diagnosticId));
        }

        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("Correlation identifier is required when supplied.", nameof(correlationId));
        }

        DiagnosticId = diagnosticId;
        Category = RequireToken(category, nameof(category));
        Code = RequireToken(code, nameof(code));
        CorrelationId = correlationId;
        FailureReason = RequireAllowlisted(failureReason, DiagnosticDetailAllowlist.FailureReasons, nameof(failureReason));
        ProviderResponseChannel = RequireAllowlisted(
            providerResponseChannel,
            DiagnosticDetailAllowlist.ResponseChannels,
            nameof(providerResponseChannel));
    }

    public Guid DiagnosticId { get; }

    public Guid? CorrelationId { get; }

    public string Category { get; }

    public string Code { get; }

    public string? FailureReason { get; }

    public string? ProviderResponseChannel { get; }

    public static bool IsSafeToken(string? value)
    {
        if (value is not { Length: > 0 and <= MaxTokenLength })
        {
            return false;
        }

        if (!char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string RequireToken(string? value, string name)
    {
        if (!IsSafeToken(value))
        {
            throw new ArgumentException(
                $"{name} must be 1-{MaxTokenLength} ASCII letters, digits, '.', '_' or '-', starting with a letter.",
                name);
        }

        return value!;
    }

    private static string? RequireAllowlisted(string? value, IReadOnlySet<string> allowlist, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!IsSafeToken(value) || !allowlist.Contains(value))
        {
            throw new ArgumentException($"{name} is not an allowlisted diagnostic token.", name);
        }

        return value;
    }
}
