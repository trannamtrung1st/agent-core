namespace AgentCore.Infrastructure.PublicWeb;

internal static class PublicWebLimits
{
    public const int MaxSearchResults = 10;
    public const int MaxSearchQueryLength = 600;
    public const int MaxRedirects = 5;
    public const int TotalTimeoutSeconds = 15;
    public const int MaxBodyBytes = 2 * 1024 * 1024;
    public const int MaxProjectedTextChars = 256 * 1024;
}
