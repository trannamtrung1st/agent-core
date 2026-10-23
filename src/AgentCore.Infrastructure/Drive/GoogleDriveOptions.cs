using AgentCore.Infrastructure.Credentials;

namespace AgentCore.Infrastructure.Drive;

public sealed class GoogleDriveOptions
{
    public bool Enabled { get; set; } = true;
    public const int MaxTransferBytes = 50 * 1024 * 1024;
}

public sealed class GoogleDriveOAuthOptions : GoogleOAuthClientOptions;
