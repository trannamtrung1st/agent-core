namespace AgentCore.Application.Email;

public sealed class EmailMonitoringOptions { public bool Enabled { get; set; } = true; }

// Infrastructure classifies permanent TLS/auth/endpoint rejection without exposing vendor objects.
public sealed class MailAuthorizationUnavailableException : Exception;
