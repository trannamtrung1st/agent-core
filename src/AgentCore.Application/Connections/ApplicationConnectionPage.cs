using AgentCore.Application.Ports;
using AgentCore.Domain.Connections;

namespace AgentCore.Application.Connections;

public static class ApplicationConnectionPageKind
{
    public const string Login = "login";
    public const string HumanVerification = "human_verification";
    public const string Application = "application";
    public const string Unknown = "unknown";

    public static string Classify(BrowserObservation observation, IReadOnlyList<string> trustedOrigins)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!OriginTrusted(observation.Url, trustedOrigins))
        {
            return Unknown;
        }

        if (observation.Intervention == BrowserInterventionKind.HumanVerificationRequired)
        {
            return HumanVerification;
        }

        if (observation.Intervention is BrowserInterventionKind.AuthenticationRequired
            or BrowserInterventionKind.AccountRegistrationRequired)
        {
            return Login;
        }

        if (IsLoginPath(observation.Url))
        {
            return Login;
        }

        return Application;
    }

    private static bool OriginTrusted(string url, IReadOnlyList<string> trustedOrigins)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var page))
        {
            return false;
        }

        foreach (var trusted in trustedOrigins)
        {
            if (Uri.TryCreate(trusted, UriKind.Absolute, out var origin)
                && string.Equals(page.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(page.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
                && page.Port == origin.Port)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLoginPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var page))
        {
            return false;
        }

        var segments = page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment.Equals("login", StringComparison.OrdinalIgnoreCase));
    }
}

public static class ApplicationConnectionPrompt
{
    public static string Format(ApplicationConnection? connection)
    {
        if (connection is null)
        {
            return string.Empty;
        }

        var authenticated = connection.Status == ApplicationConnectionStatus.Connected;
        var connected = authenticated || connection.Status == ApplicationConnectionStatus.NeedsReauthentication;
        return string.Join(
            '\n',
            "Application connection:",
            "name: " + connection.DisplayName,
            "kind: " + connection.Kind,
            "connected: " + Text(connected),
            "authenticated: " + Text(authenticated),
            "available: " + Text(authenticated),
            "trusted origin: " + Origin(connection.TrustedOrigins),
            "Use this origin exactly, including its port.");
    }

    private static string Text(bool value) => value ? "true" : "false";

    private static string Origin(IReadOnlyList<string> origins)
    {
        if (origins.Count == 0 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin))
        {
            return "unknown";
        }

        return origin.IsDefaultPort
            ? $"{origin.Scheme}://{origin.Host}"
            : $"{origin.Scheme}://{origin.Host}:{origin.Port}";
    }
}
