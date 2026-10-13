namespace AgentCore.Infrastructure.Persistence;

public sealed class CredentialRecord
{
    public string CredentialId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Purpose { get; set; } = "General";
    public EmailAccountProfileRecord? EmailAccount { get; set; }
    public DriveAccountProfileRecord? DriveAccount { get; set; }
    public string Status { get; set; } = "";
    public string MetadataJson { get; set; } = "{}";
    public string AllowedOriginsJson { get; set; } = "[]";
    public string ProtectedPayload { get; set; } = "";
    public int ProtectionVersion { get; set; }
    public long SecretRevision { get; set; }
    public long Revision { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
public sealed class AgentCredentialBindingRecord
{
    public string BindingId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string CredentialId { get; set; } = "";
    public string Reference { get; set; } = "";
    public long Revision { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}

public sealed class EmailAccountProfileRecord
{
    public string CredentialId { get; set; } = "";
    public string ProfileJson { get; set; } = "{}";
    public string? GoogleSubject { get; set; }
}

public sealed class DriveAccountProfileRecord
{
    public string CredentialId { get; set; } = "";
    public string ProfileJson { get; set; } = "{}";
    public string? GoogleSubject { get; set; }
}
