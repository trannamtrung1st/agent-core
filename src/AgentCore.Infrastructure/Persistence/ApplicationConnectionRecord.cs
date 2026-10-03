namespace AgentCore.Infrastructure.Persistence;

public sealed class ApplicationConnectionRecord
{
    public string ConnectionId { get; set; } = "";

    public string AgentInstanceId { get; set; } = "";

    public string Kind { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string BaseUrl { get; set; } = "";

    public string TrustedOriginsJson { get; set; } = "[]";

    public string Status { get; set; } = "";

    public string ProfileKey { get; set; } = "";

    public long Revision { get; set; } = 1;

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }

    public string? StatusDetail { get; set; }

    public string? WebhookKey { get; set; }

    public string? WebhookTokenHash { get; set; }

    public int WebhookStatus { get; set; }
}
